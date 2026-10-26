using BoomboCard.Payments.Contracts;
using BoomboCard.Payments.Data;
using BoomboCard.Payments.Data.Repositories;
using BoomboCard.Payments.Domain.Gateway;
using BoomboCard.Payments.Domain.PaymentAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Payments.Application.Payments.Commands;

/// <summary>
/// Applies a notification a provider pushed to us.
///
/// <para>
/// <b>Webhooks are the other half of the indeterminate problem.</b> A timeout leaves the real
/// outcome recorded at the provider; reconciliation goes and asks, and a webhook is the provider
/// volunteering. Both paths converge on the same transitions, which is why there is no separate
/// "webhook status".
/// </para>
///
/// <para>
/// <b>Nothing here is trusted on arrival.</b> Three gates, in order:
/// </para>
///
/// <list type="number">
/// <item><b>The signature</b> decides whether it came from the provider. Verified in the host,
/// where the signing secret lives, and passed in as a verdict — a module that could not verify
/// it is a module that would have to be told to trust the caller.</item>
/// <item><b>The event id</b> decides whether we have seen it before. Providers retry hard and
/// deliver out of order, so a "captured" notification arriving three times must capture once.
/// Unique at the database on <c>UX_ProviderWebhooks_EventId</c>.</item>
/// <item><b>The state machine</b> decides whether what it claims is possible. A notification
/// saying "declined" for a payment already captured is refused — that is not something a
/// provider says, so it is either a bug or an attack.</item>
/// </list>
///
/// <para>
/// A rejected webhook is still <b>recorded</b>, with its verdict. A run of failed signatures is
/// either a misconfiguration or somebody probing the endpoint, and both are worth being able to
/// see. Onyx kept no record of any provider interaction at all (<b>A17</b>).
/// </para>
/// </summary>
public sealed record ApplyProviderWebhookCommand(
    string ProviderEventId,
    WebhookKind Kind,
    string ProviderReference,
    bool SignatureVerified,
    string? Payload,
    string? ProviderCode,
    decimal? Amount,
    string? CurrencyCode,
    string? DisputeReference) : IRequest<ErrorOr<Success>>, IPaymentsRequest;

internal sealed class ApplyProviderWebhookCommandHandler(
    IPaymentRepository payments,
    IUnitOfWork<PaymentsDbContext> unitOfWork,
    TimeProvider timeProvider,
    ILogger<ApplyProviderWebhookCommandHandler> logger)
    : IRequestHandler<ApplyProviderWebhookCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ApplyProviderWebhookCommand command,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();

        var received = ProviderWebhook.Receive(
            command.ProviderEventId,
            command.Kind,
            command.ProviderReference,
            command.SignatureVerified,
            command.Payload,
            nowUtc);

        if (received.IsError)
        {
            return received.Errors;
        }

        var webhook = received.Value;

        // Gate 2 first, because it is the cheapest and the most common. Already seen means
        // already decided — returning success is correct, since the provider's retry has
        // achieved its purpose.
        if (await payments.WebhookExistsAsync(command.ProviderEventId, cancellationToken))
        {
            return Result.Success;
        }

        payments.Add(webhook);

        // Gate 1. Recorded and rejected rather than discarded: an unsigned webhook is evidence.
        if (!command.SignatureVerified)
        {
            webhook.MarkRejected("signature verification failed");

            logger.LogWarning(
                "A payment webhook for provider reference {ProviderReference} failed signature "
                + "verification and was not applied. Recorded as event {ProviderEventId}.",
                command.ProviderReference,
                command.ProviderEventId);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success;
        }

        var payment = await payments.GetByProviderReferenceAsync(
            command.ProviderReference, cancellationToken);

        if (payment is null)
        {
            // Either a payment this system never recorded — which would be serious — or traffic
            // aimed at the wrong environment. Logged as an error either way, because the first
            // possibility means money moved that we know nothing about.
            webhook.MarkRejected("no payment matches this provider reference");

            logger.LogError(
                "A verified payment webhook names provider reference {ProviderReference}, which "
                + "matches no payment in this system. Either a charge was taken that was never "
                + "recorded here, or this is traffic for another environment.",
                command.ProviderReference);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success;
        }

        // Every webhook is an interaction, so every webhook leaves an attempt row — A17.
        payment.RecordAttempt(
            OperationFor(command.Kind),
            OutcomeFor(command.Kind),
            correlationId: command.ProviderEventId,
            providerReference: command.ProviderReference,
            providerCode: command.ProviderCode,
            detail: $"webhook {command.Kind}",
            latencyMs: 0,
            nowUtc);

        // Gate 3.
        var applied = Apply(payment, command, nowUtc);

        if (applied.IsError)
        {
            webhook.MarkRejected(applied.FirstError.Code, payment.Id);

            logger.LogWarning(
                "A verified {Kind} webhook for payment {Reference} was refused by its state "
                + "machine: {Error}. The notification is recorded; the payment is unchanged.",
                command.Kind,
                payment.Reference.Value,
                applied.FirstError.Description);
        }
        else
        {
            webhook.MarkApplied(payment.Id);

            logger.LogInformation(
                "Applied a {Kind} webhook to payment {Reference}; it is now {Status}.",
                command.Kind,
                payment.Reference.Value,
                payment.Status);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }

    private static ErrorOr<Success> Apply(
        Payment payment,
        ApplyProviderWebhookCommand command,
        DateTimeOffset nowUtc)
    {
        var reference = ProviderReference.FromProvider(command.ProviderReference);

        if (reference.IsError)
        {
            return reference.Errors;
        }

        switch (command.Kind)
        {
            case WebhookKind.PaymentAuthorized:
                return payment.Authorize(reference.Value, nowUtc);

            case WebhookKind.PaymentCaptured:
                return payment.Capture(reference.Value, nowUtc);

            case WebhookKind.PaymentDeclined:
                return payment.Decline(command.ProviderCode, nowUtc);

            case WebhookKind.PaymentRefunded:
                var amount = Money.Create(
                    command.Amount ?? payment.Amount.Amount - payment.RefundedAmount.Amount,
                    command.CurrencyCode ?? payment.Amount.CurrencyCode);

                if (amount.IsError)
                {
                    return amount.Errors;
                }

                // The provider's event id doubles as the refund's idempotency key here, which is
                // exactly right: a redelivered notification carries the same id, so the unique
                // index on (PaymentId, IdempotencyKey) refunds once.
                var refund = payment.RecordRefund(
                    amount.Value,
                    reference.Value,
                    $"provider webhook {command.Kind}",
                    command.ProviderEventId,
                    nowUtc);

                return refund.IsError ? refund.Errors : Result.Success;

            case WebhookKind.PaymentChargedBack:
                return payment.RecordChargeback(
                    command.DisputeReference ?? command.ProviderEventId, nowUtc);

            default:
                return Error.Validation(
                    "Webhook.UnknownKind",
                    $"Notification kind '{command.Kind}' has no defined handling, so it is not "
                    + "applied. An unrecognised provider event is never assumed to be good news.");
        }
    }

    private static PaymentOperation OperationFor(WebhookKind kind) =>
        kind switch
        {
            WebhookKind.PaymentRefunded => PaymentOperation.Refund,
            WebhookKind.PaymentCaptured => PaymentOperation.Capture,
            _ => PaymentOperation.Authorize
        };

    private static AttemptOutcome OutcomeFor(WebhookKind kind) =>
        kind switch
        {
            WebhookKind.PaymentDeclined => AttemptOutcome.Declined,
            _ => AttemptOutcome.Succeeded
        };
}
