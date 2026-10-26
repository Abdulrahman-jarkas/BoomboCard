using System.Text.Json;
using BoomboCard.Payments.Application.Payments.Commands;
using BoomboCard.Payments.Domain.PaymentAggregate;
using BoomboCard.SharedKernel;

namespace BoomboCard.Api.Payments;

/// <summary>
/// The provider's callback.
///
/// <para>
/// <b>In the host rather than in the Payments module</b>, because it is where the signing secret
/// lives and because the raw request body is an HTTP concern. The module receives a verified
/// verdict and a parsed command, never a request.
/// </para>
///
/// <para>
/// Webhooks are the other half of the indeterminate problem. A timeout leaves the real outcome
/// recorded at the provider; <c>payments.reconciliation</c> goes and asks, and this is the
/// provider volunteering. Both paths converge on the same transitions on the same aggregate,
/// which is why there is no separate "webhook status" anywhere in the model.
/// </para>
/// </summary>
public static class PaymentWebhookEndpoint
{
    public static IEndpointRouteBuilder MapPaymentWebhookEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/payments/webhooks/provider", async (
            HttpRequest request,
            IPaymentWebhookVerifier verifier,
            ICommandDispatcher dispatcher,
            ILogger<HmacPaymentWebhookVerifier> logger,
            CancellationToken cancellationToken) =>
        {
            // The RAW body, read once and verified as sent. Re-serialising a deserialised object
            // and hashing that would change the bytes — different whitespace, different property
            // order — and the signature would never match.
            using var reader = new StreamReader(request.Body);
            var payload = await reader.ReadToEndAsync(cancellationToken);

            var verified = verifier.Verify(
                payload, request.Headers["X-Provider-Signature"].FirstOrDefault());

            ProviderNotification? notification;

            try
            {
                notification = JsonSerializer.Deserialize<ProviderNotification>(
                    payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException exception)
            {
                // Specific exception, logged with context — A16. An unparseable body is the one
                // case that cannot be recorded against an event id, because there is no event id.
                logger.LogWarning(
                    exception, "A payment webhook body could not be parsed and was rejected.");

                return Results.BadRequest();
            }

            if (notification is null
                || string.IsNullOrWhiteSpace(notification.Id)
                || string.IsNullOrWhiteSpace(notification.Reference))
            {
                return Results.BadRequest();
            }

            if (!Enum.TryParse<WebhookKind>(notification.Type, ignoreCase: true, out var kind))
            {
                // Recorded as unknown rather than acted on. An unrecognised provider event is
                // never assumed to be good news — and providers add event types without asking.
                logger.LogInformation(
                    "A payment webhook of unknown type '{Type}' was ignored.", notification.Type);

                return Results.Accepted();
            }

            // Everything else — including a failed signature — goes to the module, which records
            // it with its verdict. A run of bad signatures is either a misconfiguration or
            // somebody probing the endpoint, and both are worth being able to see.
            var result = await dispatcher.SendAsync(
                new ApplyProviderWebhookCommand(
                    notification.Id,
                    kind,
                    notification.Reference,
                    verified,
                    payload,
                    notification.Code,
                    notification.Amount,
                    notification.Currency,
                    notification.DisputeReference),
                cancellationToken);

            // 202 whatever happened, provided the request was well formed.
            //
            // Deliberate: a provider that gets a 4xx or 5xx retries, and for a notification we
            // have already handled — or refused on purpose, or cannot match — retrying forever
            // achieves nothing and eventually gets the endpoint disabled at the provider's end.
            // The record of what was rejected and why is in ProviderWebhooks.
            return result.IsError ? Results.Accepted() : Results.Accepted();
        })
        // Anonymous, and one of the few routes that says so out loud: the global fallback policy
        // requires an authenticated principal, and a payment provider has no account here. The
        // signature is the authentication, which is why it is verified before anything is applied.
        .AllowAnonymous()
        .WithTags("Payments")
        .WithName("ProviderPaymentWebhook")
        .WithSummary("Receives payment notifications from the provider. Signature-verified.")
        .WithDescription(
            "Anonymous by necessity — a payment provider has no account here — and authenticated "
            + "by **HMAC-SHA256 over the raw request body**, compared in fixed time. Fixed-time "
            + "comparison matters more here than for a password: an attacker can submit webhook "
            + "signatures as fast as the network allows.\n\n"
            + "**Three gates, in order.** The signature decides whether it came from the "
            + "provider; the provider's own event id decides whether we have seen it before "
            + "(unique at the database, because providers retry hard and deliver out of order); "
            + "and the payment's state machine decides whether what it claims is possible. A "
            + "notification saying \"declined\" for a payment already captured is refused — that "
            + "is not something a provider says, so it is either a bug or an attack.\n\n"
            + "**A rejected webhook is still recorded**, with its verdict and its raw payload. "
            + "When a chargeback is disputed six months later, the thing that settles it is what "
            + "the provider actually sent. Onyx **A17** kept no record of any provider "
            + "interaction at all.\n\n"
            + "Always 202 for a well-formed request, including one that was rejected: a provider "
            + "that gets an error retries, and retrying a notification we have deliberately "
            + "refused achieves nothing except eventually getting the endpoint disabled.")
        .ExcludeFromDescription();

        return app;
    }

    /// <summary>
    /// What a provider posts. Only the fields this system acts on — a provider's payload carries
    /// far more, and the full body is kept verbatim on the webhook row rather than modelled.
    /// </summary>
    private sealed record ProviderNotification(
        string? Id,
        string? Type,
        string? Reference,
        string? Code,
        decimal? Amount,
        string? Currency,
        string? DisputeReference);
}
