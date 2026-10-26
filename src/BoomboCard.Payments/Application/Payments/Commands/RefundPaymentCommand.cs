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

public sealed record RefundResponse(
    Guid PaymentId,
    string Status,
    decimal RefundedAmount,
    decimal RemainingAmount,
    string CurrencyCode);

/// <summary>
/// Gives money back, in whole or in part.
///
/// <para>
/// Same three-step shape as <see cref="SubmitPaymentCommand"/> and <b>deliberately not an
/// <see cref="IPaymentsRequest"/></b> for the same reason: it calls a provider, and a transaction
/// must never be held across that call (<b>A12</b>).
/// </para>
///
/// <para>
/// <paramref name="Amount"/> is nullable and means "all of it" when omitted — so the common case
/// cannot get the figure wrong, and a partial refund has to state its figure explicitly. The
/// amount is then <b>capped by what the payment actually captured</b>, summed from the refund
/// rows inside the aggregate rather than trusted from here. That is the lesson of
/// BUGS-AND-LESSONS §13 applied to money instead of stock: a caller that decides how much it may
/// give back is a caller being trusted with somebody else's money.
/// </para>
///
/// <para>
/// <paramref name="IdempotencyKey"/> is the <i>refund's</i> own key, not the payment's. Two
/// refunds against one payment are two separate provider transactions and must deduplicate
/// independently — unique at the database on <c>(PaymentId, IdempotencyKey)</c>.
/// </para>
/// </summary>
public sealed record RefundPaymentCommand(
    Guid PaymentId,
    decimal? Amount,
    string Reason,
    string IdempotencyKey) : IRequest<ErrorOr<RefundResponse>>;

internal sealed class RefundPaymentCommandHandler(
    IPaymentRepository payments,
    IPaymentGateway gateway,
    ICommandDispatcher dispatcher,
    ILogger<RefundPaymentCommandHandler> logger)
    : IRequestHandler<RefundPaymentCommand, ErrorOr<RefundResponse>>
{
    public async Task<ErrorOr<RefundResponse>> Handle(
        RefundPaymentCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            return PaymentErrors.RefundKeyRequired;
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Error.Validation(
                "Payment.RefundReasonRequired",
                "A refund must say why, so a statement can explain the credit.");
        }

        // ---- step 1: read.
        var payment = await payments.GetAsync(command.PaymentId, cancellationToken);

        if (payment is null)
        {
            return PaymentErrors.NotFound(command.PaymentId);
        }

        if (payment.ProviderReference is null)
        {
            // Nothing to refund against. This is the state Onyx M4 made indistinguishable from a
            // real payment by storing a local confirmation code in this field — and the register
            // lists "refunds are impossible" as a direct consequence.
            return Error.Conflict(
                "Payment.NoProviderReference",
                "This payment has no provider reference, so there is no transaction to refund.");
        }

        // Already applied, so the provider must not be called again. The read is the fast path;
        // UX_Refunds_Idempotency is the guarantee.
        if (payment.Refunds.Any(refund => refund.IdempotencyKey == command.IdempotencyKey))
        {
            // Safe: nothing has been called and nothing has changed it, so this context's view IS
            // the current state.
            return Describe(payment);
        }

        var remaining = payment.Amount.Amount - payment.RefundedAmount.Amount;

        // Omitted means "all of it". Stated explicitly rather than defaulted to zero, because a
        // zero-amount refund is a silent no-op and this is money.
        var requested = command.Amount ?? remaining;

        var amount = Money.Create(requested, payment.Amount.CurrencyCode);

        if (amount.IsError)
        {
            return amount.Errors;
        }

        // Checked before calling the provider as well as inside the aggregate. Not redundant:
        // this one keeps a request the provider would reject off the wire, and the aggregate's is
        // the one that cannot be bypassed.
        if (requested > remaining)
        {
            return PaymentErrors.RefundExceedsCapture(remaining, payment.Amount.CurrencyCode);
        }

        // ---- step 2: the provider call, OUTSIDE every transaction.
        var result = await gateway.RefundAsync(
            new GatewayRefundRequest(
                payment.Id,
                command.IdempotencyKey,
                payment.ProviderReference.Value,
                amount.Value.Amount,
                amount.Value.CurrencyCode,
                command.Reason),
            cancellationToken);

        if (!result.Succeeded)
        {
            // A failed refund leaves the payment untouched, which is the safe direction: the
            // customer has not been credited and the data says so. The attempt is still recorded
            // so support can see it was tried.
            await dispatcher.SendAsync(
                new RecordPaymentOutcomeCommand(payment.Id, PaymentOperation.Refund, result),
                cancellationToken);

            logger.LogError(
                "Refund of {Amount} {Currency} for payment {Reference} did not succeed "
                + "({Outcome}, {ProviderCode}). Correlation {CorrelationId}.",
                amount.Value.Amount,
                amount.Value.CurrencyCode,
                payment.Reference.Value,
                result.Outcome,
                result.ProviderCode ?? "no code",
                result.CorrelationId);

            return Error.Failure(
                "Payment.RefundNotAccepted",
                result.Outcome is AttemptOutcome.Indeterminate
                    ? "The provider did not confirm the refund. Do not retry — reconciliation "
                      + "will establish whether it went through."
                    : "The provider did not accept the refund.");
        }

        // ---- step 3: record it, in its own short transaction.
        var recorded = await dispatcher.SendAsync(
            new RecordRefundCommand(
                payment.Id,
                amount.Value.Amount,
                amount.Value.CurrencyCode,
                command.Reason,
                command.IdempotencyKey,
                result),
            cancellationToken);

        if (recorded.IsError)
        {
            logger.LogError(
                "The provider accepted a refund of {Amount} {Currency} for payment {Reference} "
                + "and it could not be recorded: {Error}. Correlation {CorrelationId}. The money "
                + "has left; reconciliation must repair the record.",
                amount.Value.Amount,
                amount.Value.CurrencyCode,
                payment.Reference.Value,
                recorded.FirstError.Description,
                result.CorrelationId);

            return recorded.Errors;
        }

        // FROM the command, not a re-read. This handler's DbContext has the payment tracked from
        // step 1 and the write happened in another scope, so re-reading here returned the state
        // as it was BEFORE the refund — a correct write with a stale answer. BUGS-AND-LESSONS
        // section 14.
        return recorded.Value;
    }

    internal static RefundResponse Describe(Payment payment) =>
        new(
            payment.Id,
            payment.Status.ToString(),
            payment.RefundedAmount.Amount,
            payment.Amount.Amount - payment.RefundedAmount.Amount,
            payment.Amount.CurrencyCode);
}

/// <summary>
/// Writes a refund the provider has already accepted. Marked, so it gets its own transaction
/// with no external call inside it.
/// </summary>
public sealed record RecordRefundCommand(
    Guid PaymentId,
    decimal Amount,
    string CurrencyCode,
    string Reason,
    string IdempotencyKey,
    GatewayResult Result) : IRequest<ErrorOr<RefundResponse>>, IPaymentsRequest;

internal sealed class RecordRefundCommandHandler(
    IPaymentRepository payments,
    IUnitOfWork<PaymentsDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<RecordRefundCommand, ErrorOr<RefundResponse>>
{
    public async Task<ErrorOr<RefundResponse>> Handle(
        RecordRefundCommand command,
        CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(command.PaymentId, cancellationToken);

        if (payment is null)
        {
            return PaymentErrors.NotFound(command.PaymentId);
        }

        var amount = Money.Create(command.Amount, command.CurrencyCode);

        if (amount.IsError)
        {
            return amount.Errors;
        }

        // The refund's own provider reference, from the refund response. A separate transaction
        // at the provider needs a separate reference, or reconciliation cannot match it.
        var reference = ProviderReference.FromProvider(command.Result.ProviderReference);

        if (reference.IsError)
        {
            return reference.Errors;
        }

        var nowUtc = timeProvider.GetUtcNow();

        payment.RecordAttempt(
            PaymentOperation.Refund,
            command.Result.Outcome,
            command.Result.CorrelationId,
            command.Result.ProviderReference,
            command.Result.ProviderCode,
            command.Result.Detail,
            command.Result.LatencyMs,
            nowUtc);

        var refund = payment.RecordRefund(
            amount.Value, reference.Value, command.Reason, command.IdempotencyKey, nowUtc);

        if (refund.IsError)
        {
            return refund.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Returned from the handler that changed it, not re-read by the caller — see
        // PaymentOutcomeRecorded and BUGS-AND-LESSONS section 14.
        return RefundPaymentCommandHandler.Describe(payment);
    }
}
