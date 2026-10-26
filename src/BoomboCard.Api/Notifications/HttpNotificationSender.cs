using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BoomboCard.Notifications.Domain.NotificationAggregate;
using BoomboCard.Notifications.Domain.Sending;
using Microsoft.Extensions.Options;

namespace BoomboCard.Api.Notifications;

/// <summary>
/// Everything the host needs to reach an email provider. Validated at startup.
///
/// <para>
/// The same discipline as <c>PaymentGatewayOptions</c>, and the same findings behind it: Onyx
/// <b>A15</b> bound a base URL from configuration and hardcoded the real one anyway, and
/// <b>S17</b> bound an API key and never injected it.
/// </para>
/// </summary>
public sealed class NotificationSenderOptions
{
    public const string SectionName = "NotificationSender";

    /// <summary>
    /// <c>true</c> to log notifications instead of sending them. Development only — see
    /// <see cref="Validate"/>.
    /// </summary>
    public bool UseLogging { get; init; }

    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Who messages come from. Required, because a provider will reject a blank sender.</summary>
    public string FromAddress { get; init; } = string.Empty;

    public string FromName { get; init; } = "BoomboCard";

    /// <summary>
    /// Per-attempt timeout. Ten seconds, not the <c>HttpClient</c> default of 100 — a queued
    /// notification being retried in a minute is strictly better than a dispatcher thread parked
    /// for a minute and a half.
    /// </summary>
    [Range(1, 60)]
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Checks what data annotations cannot.
    ///
    /// <para>
    /// The logging sender is refused outside Development for the same reason the stub payment
    /// gateway is: it approves everything and sends nothing, so in production it would reproduce
    /// Onyx <b>A8</b> exactly — every notification recorded as delivered, no customer ever
    /// contacted. The difference is that here the delivery log would say <c>logging</c> on every
    /// row, which is why that column exists.
    /// </para>
    /// </summary>
    public void Validate(bool isDevelopment)
    {
        if (UseLogging && !isDevelopment)
        {
            throw new InvalidOperationException(
                "NotificationSender:UseLogging is set outside Development. That sender records "
                + "every notification as delivered and transmits nothing, which is Onyx A8 — no "
                + "customer ever receives anything — reached deliberately. It is available in "
                + "Development only.");
        }

        if (UseLogging)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FromAddress))
        {
            throw new InvalidOperationException(
                "NotificationSender:FromAddress is required. A provider will reject every message "
                + "without one, and a rejection per message is an outage that looks like bad data.");
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException(
                "NotificationSender:ApiKey is required. Onyx S17 bound a gateway API key from "
                + "configuration and never injected it, so every request was unauthenticated — "
                + "and because the result was never inspected, every rejection was recorded as a "
                + "success.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "NotificationSender:BaseUrl must be an absolute https URL. A notification body "
                + "can contain a single-use password-reset token, so it must never cross a "
                + "network in clear text.");
        }
    }
}

/// <summary>
/// Attaches the provider credential to every outgoing request.
///
/// <para>
/// A handler rather than a line in the send method, for the reason <b>S17</b> demonstrates: a
/// call site can forget a header, and a handler in the pipeline is not the call site's
/// responsibility to remember.
/// </para>
/// </summary>
internal sealed class NotificationCredentialHandler(IOptions<NotificationSenderOptions> options)
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
/// Sends notifications through an email provider's HTTP API.
///
/// <para>
/// The same shape as <c>HttpPaymentGateway</c> and for the same reasons — a typed client from
/// <c>IHttpClientFactory</c> with a pooled handler (<b>A14</b>), the base address from validated
/// options (<b>A15</b>), the credential attached by a delegating handler (<b>S17</b>), specific
/// exception types each mapped to the outcome it implies (<b>A16</b>), and <b>no certificate
/// callback anywhere</b> (<b>S8</b>, banned solution-wide by a source-convention test).
/// </para>
///
/// <para>
/// <b>The mapping is three-way rather than four-way, and that is the one real difference.</b>
/// Payments needs an <c>Indeterminate</c> outcome, an idempotency key and a reconciliation job,
/// because a lost response to a charge means the customer may have been billed and only the
/// provider knows. A lost response to an email means the customer may get it twice. So a timeout
/// here is a transport failure, the notification is retried, and the duplicate is accepted as the
/// cheaper of the two failures — see <see cref="DeliveryOutcome"/>.
/// </para>
/// </summary>
internal sealed class HttpNotificationSender(
    HttpClient client,
    IOptions<NotificationSenderOptions> options,
    ILogger<HttpNotificationSender> logger) : INotificationSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "http";

    public async Task<SendResult> SendAsync(
        NotificationEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();

        // Relative, because the base address comes from validated options. There is no absolute
        // URL anywhere in this file — A15.
        using var request = new HttpRequestMessage(HttpMethod.Post, "messages");

        request.Headers.Add("X-Correlation-Id", correlationId);

        request.Content = JsonContent.Create(
            new
            {
                from = new { email = options.Value.FromAddress, name = options.Value.FromName },
                to = new { email = envelope.Recipient.Value, name = envelope.Recipient.DisplayName },
                subject = envelope.Subject,
                text = envelope.Body
            },
            options: Json);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);

            stopwatch.Stop();

            return Map(response, await ReadAsync(response, cancellationToken), stopwatch, correlationId);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            // A timeout. Treated as a transport failure, NOT as an unknown outcome.
            //
            // The provider may well have accepted the message, so a retry may send it twice —
            // and that is the deliberate trade. The alternative is Payments' machinery: an
            // idempotency key, an enquiry endpoint and a reconciliation job, built because a
            // duplicate CHARGE is theft. A duplicate email is an apology.
            logger.LogWarning(
                exception,
                "The notification provider did not answer within {TimeoutSeconds}s for "
                + "notification {NotificationId}. Retrying; a duplicate is possible and "
                + "acceptable. Correlation {CorrelationId}.",
                options.Value.TimeoutSeconds,
                envelope.NotificationId,
                correlationId);

            return Failure(
                $"timeout after {options.Value.TimeoutSeconds}s", stopwatch, correlationId);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();

            // Connection refused, DNS, TLS handshake failure. Reported as a failure — which is
            // the correct behaviour and the inverse of S8, where Onyx disabled certificate
            // validation precisely so this case would silently succeed.
            logger.LogWarning(
                exception,
                "Could not reach the notification provider for {NotificationId} ({StatusCode}). "
                + "Correlation {CorrelationId}.",
                envelope.NotificationId,
                exception.StatusCode,
                correlationId);

            return Failure(
                $"{exception.GetType().Name}: {exception.Message}", stopwatch, correlationId);
        }
        catch (JsonException exception)
        {
            stopwatch.Stop();

            // A 2xx we cannot parse. The message was probably accepted, but we have no provider
            // id to record — so it is a failure rather than an accept, because recording a
            // delivery we cannot evidence is the first step towards A8's "nothing records that".
            logger.LogError(
                exception,
                "The notification provider returned an unparseable response for "
                + "{NotificationId}. Correlation {CorrelationId}.",
                envelope.NotificationId,
                correlationId);

            return Failure($"unparseable response: {exception.Message}", stopwatch, correlationId);
        }
    }

    /// <summary>Maps a response to an outcome. No default that drifts towards success.</summary>
    private SendResult Map(
        HttpResponseMessage response,
        ProviderPayload? payload,
        Stopwatch stopwatch,
        string correlationId)
    {
        var latency = (int)stopwatch.ElapsedMilliseconds;

        if (response.IsSuccessStatusCode)
        {
            // A 2xx whose body says the message was rejected is a rejection, not a success.
            // Providers routinely report a blocked address this way, and reading the status alone
            // is how a bounce becomes a delivery in your own records.
            if (payload?.Status is "rejected" or "bounced" or "blocked")
            {
                return new SendResult(
                    DeliveryOutcome.Rejected,
                    payload.Id,
                    payload.Message ?? payload.Status,
                    latency,
                    correlationId,
                    Name);
            }

            return new SendResult(
                DeliveryOutcome.Accepted,
                payload?.Id,
                payload?.Message,
                latency,
                correlationId,
                Name);
        }

        // 400 and 422 mean the provider refused the request itself — a malformed or blocked
        // address. Definitive: retrying an identical bad request cannot help, and five attempts
        // at a blocked domain is five chances to damage a sending reputation.
        if (response.StatusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.UnprocessableEntity)
        {
            return new SendResult(
                DeliveryOutcome.Rejected,
                payload?.Id,
                payload?.Message ?? $"HTTP {(int)response.StatusCode}",
                latency,
                correlationId,
                Name);
        }

        // 401/403 means OUR credential is wrong, which is S17's symptom. Not a rejection — there
        // is nothing wrong with the recipient — so it is retried, and logged as an error because
        // it is an outage we caused and every notification will fail until it is fixed.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            logger.LogError(
                "The notification provider rejected our credential ({StatusCode}). NO "
                + "notification can be delivered until this is fixed. Correlation "
                + "{CorrelationId}.",
                (int)response.StatusCode,
                correlationId);

            return Failure(
                $"provider rejected our credential: HTTP {(int)response.StatusCode}",
                stopwatch,
                correlationId);
        }

        // 429 and 5xx. Transient by definition, and the backoff is what makes a rate limit
        // self-correcting rather than a tight loop.
        return Failure($"HTTP {(int)response.StatusCode}", stopwatch, correlationId);
    }

    private SendResult Failure(string detail, Stopwatch stopwatch, string correlationId) =>
        new(
            DeliveryOutcome.TransportFailure,
            null,
            detail,
            (int)stopwatch.ElapsedMilliseconds,
            correlationId,
            Name);

    private static async Task<ProviderPayload?> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is 0)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<ProviderPayload>(Json, cancellationToken);
    }

    /// <summary>What a provider sends back, reduced to the three fields that matter.</summary>
    private sealed record ProviderPayload(string? Id, string? Status, string? Message);
}
