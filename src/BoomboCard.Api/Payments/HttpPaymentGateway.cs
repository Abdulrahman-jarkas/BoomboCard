using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoomboCard.Payments.Domain.Gateway;
using BoomboCard.Payments.Domain.PaymentAggregate;
using Microsoft.Extensions.Options;

namespace BoomboCard.Api.Payments;

/// <summary>
/// Attaches the provider credential to every outgoing request.
///
/// <para>
/// <b>Onyx S17:</b> the API key was bound from configuration and never injected into the client.
/// <c>ChargeAsync</c> sent no credential at all, so every charge would have been rejected as
/// unauthenticated — and because the result was never inspected (<b>M1</b>), every rejection was
/// recorded as a successful payment.
/// </para>
///
/// <para>
/// <b>A handler rather than a line in each method</b>, which is the point. A call site can forget
/// a header; a handler in the pipeline cannot be forgotten by a new call site, because it is not
/// the call site's responsibility. The same reasoning as the versioning interceptor: put the
/// thing that must always happen somewhere it happens by construction.
/// </para>
/// </summary>
internal sealed class GatewayCredentialHandler(IOptions<PaymentGatewayOptions> options)
    : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// The HTTP adapter for a payment provider.
///
/// <para>
/// <b>Five Onyx findings live in the class this replaces</b>, and the shape of each fix is worth
/// naming because none of them is clever:
/// </para>
///
/// <list type="bullet">
/// <item><b>S8</b> — <c>ServerCertificateCustomValidationCallback = (_, _, _, _) =&gt; true</c>,
/// accepting any certificate on the payment gateway. <b>There is no certificate callback in this
/// file, and a source-convention test bans the pattern across the whole solution.</b> If a
/// sandbox needs a private CA, that CA is trusted at the machine level in that environment —
/// never in code, because code ships everywhere.</item>
/// <item><b>A14</b> — <c>new HttpClientHandler</c> and <c>new HttpClient(handler)</c> on every
/// call, inside a retry loop, each disposal leaving a socket in <c>TIME_WAIT</c> until the
/// ephemeral port range ran out. A typed client from <c>IHttpClientFactory</c> with a pooled
/// handler, injected, never constructed.</item>
/// <item><b>A15</b> — the configured base address ignored and the URL hardcoded. The base
/// address comes from validated options; this class contains no absolute URL.</item>
/// <item><b>S17</b> — the credential never sent. Attached by
/// <see cref="GatewayCredentialHandler"/>.</item>
/// <item><b>A16</b> — <c>catch { }</c> in the retry loop and
/// <c>catch (Exception ex) { Console.WriteLine(...); return new PaymentGatewayResult { Success = false }; }</c>
/// in the client. <b>Specific exception types, each mapped to the outcome it actually implies</b>,
/// with the correlation id on every result.</item>
/// </list>
///
/// <para>
/// <b>The hard part is not any of those.</b> It is that a timeout and a decline must not produce
/// the same answer. <c>TaskCanceledException</c> after the request was sent means the provider may
/// have charged the card and we lost the reply — <see cref="AttemptOutcome.Indeterminate"/> —
/// while a connection refused means it never saw the request at all —
/// <see cref="AttemptOutcome.TransportFailure"/>. Onyx's <c>bool Success</c> could express
/// neither, which is how it managed to read a lost response as success in one place and as a
/// reason to charge again in another.
/// </para>
/// </summary>
internal sealed class HttpPaymentGateway(
    HttpClient client,
    IOptions<PaymentGatewayOptions> options,
    ILogger<HttpPaymentGateway> logger) : IPaymentGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<GatewayResult> AuthorizeAsync(
        GatewayAuthorizeRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            "charges",
            request.IdempotencyKey,
            new
            {
                amount = ToMinorUnits(request.Amount),
                currency = request.CurrencyCode,
                source = request.Instrument.Token,
                description = request.Description,
                capture = true
            },
            cancellationToken);

    public Task<GatewayResult> CaptureAsync(
        GatewayCaptureRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            $"charges/{Uri.EscapeDataString(request.ProviderReference)}/capture",
            request.IdempotencyKey,
            new { amount = ToMinorUnits(request.Amount), currency = request.CurrencyCode },
            cancellationToken);

    public Task<GatewayResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            "refunds",
            request.IdempotencyKey,
            new
            {
                charge = request.ProviderReference,
                amount = ToMinorUnits(request.Amount),
                currency = request.CurrencyCode,
                reason = request.Reason
            },
            cancellationToken);

    /// <summary>
    /// "What happened to the request carrying this idempotency key?"
    ///
    /// <para>
    /// A GET, so it is safe to repeat — which matters, because the reconciliation job calls it on
    /// a timer against payments whose outcome is unknown.
    /// </para>
    /// </summary>
    public Task<GatewayResult> EnquireAsync(
        GatewayEnquiryRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Get,
            $"charges/by-idempotency-key/{Uri.EscapeDataString(request.IdempotencyKey)}",
            request.IdempotencyKey,
            body: null,
            cancellationToken);

    /// <summary>
    /// One request, and the mapping from everything that can happen to one of four outcomes.
    ///
    /// <para>
    /// <b>No exception escapes this method</b>, and that is deliberate rather than lazy: the
    /// interface promises a <see cref="GatewayResult"/>, and the only place with enough context
    /// to turn <c>SocketException</c> into "the provider never saw it" is here, where the
    /// exception type is known. An exception reaching the caller would mean the caller had to
    /// guess — which is exactly what Onyx's <c>catch { }</c> forced it to do.
    /// </para>
    ///
    /// <para>
    /// Everything is logged with the correlation id before being returned, so a log line, an
    /// attempt row and the provider's own record of the request all join up (<b>A16</b>).
    /// </para>
    /// </summary>
    private async Task<GatewayResult> SendAsync(
        HttpMethod method,
        string path,
        string idempotencyKey,
        object? body,
        CancellationToken cancellationToken)
    {
        // Ours, per request. Sent as a header so the provider's logs carry it too.
        var correlationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();

        // Relative, because the base address comes from validated options. There is no absolute
        // URL anywhere in this file — A15.
        using var request = new HttpRequestMessage(method, path);

        // THE IDEMPOTENCY KEY, on every request, from the caller. M5.
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-Correlation-Id", correlationId);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);

            stopwatch.Stop();

            var payload = await ReadAsync(response, cancellationToken);

            return Map(response, payload, stopwatch, correlationId);
        }
        catch (TaskCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            // THE IMPORTANT CASE.
            //
            // A timeout, not a cancellation — the request went out and we never learned the
            // result. The card may well have been charged. This is NOT a failure, and reporting
            // it as one is half of Onyx M5; reporting it as a success is Onyx M1.
            logger.LogError(
                exception,
                "The payment provider did not answer {Method} {Path} within the timeout "
                + "({LatencyMs}ms). The outcome is UNKNOWN — the charge may have succeeded. "
                + "Correlation {CorrelationId}, idempotency key {IdempotencyKey}.",
                method,
                path,
                stopwatch.ElapsedMilliseconds,
                correlationId,
                idempotencyKey);

            return new GatewayResult(
                AttemptOutcome.Indeterminate,
                ProviderReference: null,
                ProviderCode: null,
                Detail: $"timeout after {options.Value.TimeoutSeconds}s",
                (int)stopwatch.ElapsedMilliseconds,
                correlationId);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();

            // Connection refused, DNS failure, TLS handshake failure. The provider never saw the
            // request, so the outcome IS known: nothing happened. Safe to retry.
            //
            // Note that a TLS failure lands here and is reported as a failure, which is the
            // correct behaviour and the opposite of S8 — Onyx disabled certificate validation so
            // that this case would silently succeed instead.
            logger.LogWarning(
                exception,
                "Could not reach the payment provider for {Method} {Path} after {LatencyMs}ms "
                + "({StatusCode}). Correlation {CorrelationId}.",
                method,
                path,
                stopwatch.ElapsedMilliseconds,
                exception.StatusCode,
                correlationId);

            return new GatewayResult(
                AttemptOutcome.TransportFailure,
                null,
                null,
                $"{exception.GetType().Name}: {exception.Message}",
                (int)stopwatch.ElapsedMilliseconds,
                correlationId);
        }
        catch (JsonException exception)
        {
            stopwatch.Stop();

            // A 200 we cannot parse. Indeterminate rather than a failure: the provider answered
            // positively enough to return 200, so assuming nothing happened would be the M5
            // mistake in a new costume.
            logger.LogError(
                exception,
                "The payment provider returned a response for {Method} {Path} that could not be "
                + "parsed, so the outcome is UNKNOWN. Correlation {CorrelationId}.",
                method,
                path,
                correlationId);

            return new GatewayResult(
                AttemptOutcome.Indeterminate,
                null,
                null,
                $"unparseable response: {exception.Message}",
                (int)stopwatch.ElapsedMilliseconds,
                correlationId);
        }
    }

    /// <summary>
    /// Maps a response to an outcome. <b>No default that drifts towards success.</b>
    /// </summary>
    private GatewayResult Map(
        HttpResponseMessage response,
        GatewayPayload? payload,
        Stopwatch stopwatch,
        string correlationId)
    {
        var latency = (int)stopwatch.ElapsedMilliseconds;

        if (response.IsSuccessStatusCode)
        {
            // A 200 whose body says "declined" is a decline, not a success. Providers routinely
            // report declines this way, and reading the HTTP status alone is how a declined card
            // becomes a paid order.
            if (payload?.Status is "declined" or "failed")
            {
                logger.LogInformation(
                    "The provider declined the charge with {ProviderCode}. Correlation "
                    + "{CorrelationId}.",
                    payload.Code ?? "no code",
                    correlationId);

                return new GatewayResult(
                    AttemptOutcome.Declined,
                    payload.Id,
                    payload.Code,
                    payload.Message,
                    latency,
                    correlationId);
            }

            // Success with no reference is a protocol violation. Reported as indeterminate rather
            // than success, because ProviderReference.FromProvider will refuse it anyway and the
            // payment must not settle — M4 holding even against a misbehaving provider.
            if (string.IsNullOrWhiteSpace(payload?.Id))
            {
                logger.LogError(
                    "The provider returned {StatusCode} with no transaction reference, so the "
                    + "outcome cannot be recorded as settled. Correlation {CorrelationId}.",
                    (int)response.StatusCode,
                    correlationId);

                return new GatewayResult(
                    AttemptOutcome.Indeterminate,
                    null,
                    payload?.Code,
                    "provider returned success with no reference",
                    latency,
                    correlationId);
            }

            return new GatewayResult(
                AttemptOutcome.Succeeded,
                payload.Id,
                payload.Code,
                payload.Message,
                latency,
                correlationId);
        }

        // 402 Payment Required is the conventional decline status. 400 and 422 are the provider
        // rejecting the request itself — a bad token, an unsupported currency — which is also
        // definitive: retrying an identical bad request cannot help.
        if (response.StatusCode is HttpStatusCode.PaymentRequired
            or HttpStatusCode.BadRequest
            or HttpStatusCode.UnprocessableEntity)
        {
            return new GatewayResult(
                AttemptOutcome.Declined,
                payload?.Id,
                payload?.Code,
                payload?.Message ?? $"HTTP {(int)response.StatusCode}",
                latency,
                correlationId);
        }

        // 401/403 means OUR credential is wrong, which is S17's symptom. Not a decline — the
        // customer's card is fine and nothing about their payment is at fault — so it is a
        // transport failure, and it is logged as an error because it is an outage we caused.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            logger.LogError(
                "The payment provider rejected our credential ({StatusCode}). No payment can "
                + "succeed until this is fixed. Correlation {CorrelationId}.",
                (int)response.StatusCode,
                correlationId);

            return new GatewayResult(
                AttemptOutcome.TransportFailure,
                null,
                null,
                $"provider rejected our credential: HTTP {(int)response.StatusCode}",
                latency,
                correlationId);
        }

        // 404 on an enquiry means the provider has no record of that idempotency key, which is a
        // real answer: the request never landed. Anything else 4xx/5xx is transport.
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return new GatewayResult(
                AttemptOutcome.TransportFailure,
                null,
                null,
                "the provider has no record of this request",
                latency,
                correlationId);
        }

        // 5xx and everything unlisted. A 5xx AFTER the request was accepted may mean the charge
        // went through, so it is indeterminate rather than a failure — the conservative direction
        // for money.
        logger.LogWarning(
            "The payment provider returned {StatusCode} after {LatencyMs}ms; treating the "
            + "outcome as unknown. Correlation {CorrelationId}.",
            (int)response.StatusCode,
            latency,
            correlationId);

        return new GatewayResult(
            AttemptOutcome.Indeterminate,
            payload?.Id,
            payload?.Code,
            payload?.Message ?? $"HTTP {(int)response.StatusCode}",
            latency,
            correlationId);
    }

    private static async Task<GatewayPayload?> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is 0)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<GatewayPayload>(Json, cancellationToken);
    }

    /// <summary>
    /// Providers transact in minor units — 1999 rather than 19.99 — because an integer cannot
    /// acquire a fractional cent in transit. Both supported currencies are two-decimal, and
    /// <c>Money</c> has already rounded, so this is exact.
    /// </summary>
    private static long ToMinorUnits(decimal amount) => (long)decimal.Round(amount * 100m, 0);

    /// <summary>What a provider sends back, reduced to the four fields that matter.</summary>
    private sealed record GatewayPayload(
        string? Id,
        string? Status,
        string? Code,
        string? Message);
}
