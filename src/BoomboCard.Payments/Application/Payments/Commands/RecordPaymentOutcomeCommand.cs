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
/// Applies a provider's answer to the aggregate. <b>The only place a payment's status moves, and
/// the only place a provider reference is ever set.</b>
///
/// <para>
/// This is the heart of <b>M1</b>. Onyx's controller did this instead:
/// </para>
///
/// <code>
/// // result may be null, or Success == false — neither is checked
/// var payment = new Payment { ..., Status = "Paid", ... };
/// order.Status = OrderStatus.Paid;
/// </code>
///
/// <para>
/// Here the four outcomes map to four different transitions and there is no default case that
/// drifts towards success. The compiler enforces the exhaustiveness, and
/// <see cref="ProviderReference.FromProvider"/> enforces that a settled status cannot be reached
/// without an answer — a null or blank reference is rejected rather than substituted, which is
/// <b>M4</b>.
/// </para>
///
/// <para>
/// Marked <see cref="IPaymentsRequest"/>, so it runs in its own short transaction with no
/// external call inside it. The attempt row and the status change commit together, which is what
/// makes the audit trail (<b>A17</b>) agree with the state it describes.
/// </para>
/// </summary>
/// <param name="PaymentId">Which payment.</param>
/// <param name="Operation">What was being attempted.</param>
/// <param name="Result">What the provider said.</param>
public sealed record RecordPaymentOutcomeCommand(
    Guid PaymentId,
    PaymentOperation Operation,
    GatewayResult Result) : IRequest<ErrorOr<PaymentOutcomeRecorded>>, IPaymentsRequest;

/// <summary>
/// The payment's state <b>after</b> the outcome was applied.
///
/// <para>
/// <b>Returned rather than re-read, and that is a bug fix.</b> The orchestrating handler used to
/// re-read the payment through its own repository afterwards and describe that — but its
/// <c>DbContext</c> had the aggregate tracked from step 1, EF's identity map returned the tracked
/// instance, and the write had happened in a different scope. So the response said <c>Pending</c>
/// for a payment that was actually <c>Captured</c>: a correct write with an answer that described
/// the state before it.
/// </para>
///
/// <para>
/// Returning the state from the command that changed it removes the second read entirely, so
/// there is nothing left to be stale. BUGS-AND-LESSONS section 14.
/// </para>
/// </summary>
public sealed record PaymentOutcomeRecorded(
    string Status,
    string? DeclineCode,
    bool CanSubmit,
    bool OutcomeIsUnknown);

internal sealed class RecordPaymentOutcomeCommandHandler(
    IPaymentRepository payments,
    IUnitOfWork<PaymentsDbContext> unitOfWork,
    TimeProvider timeProvider,
    ILogger<RecordPaymentOutcomeCommandHandler> logger)
    : IRequestHandler<RecordPaymentOutcomeCommand, ErrorOr<PaymentOutcomeRecorded>>
{
    public async Task<ErrorOr<PaymentOutcomeRecorded>> Handle(
        RecordPaymentOutcomeCommand command,
        CancellationToken cancellationToken)
    {
        var payment = await payments.GetAsync(command.PaymentId, cancellationToken);

        if (payment is null)
        {
            return PaymentErrors.NotFound(command.PaymentId);
        }

        var nowUtc = timeProvider.GetUtcNow();
        var result = command.Result;

        // The attempt row FIRST, and unconditionally.
        //
        // Written whatever the outcome and whether or not the transition below succeeds, because
        // the history is only useful if it is complete — A17. It commits in the same transaction
        // as the status change, so "what we did" and "what happened" cannot disagree.
        payment.RecordAttempt(
            command.Operation,
            result.Outcome,
            result.CorrelationId,
            result.ProviderReference,
            result.ProviderCode,
            result.Detail,
            result.LatencyMs,
            nowUtc);

        var applied = Apply(payment, command.Operation, result, nowUtc);

        if (applied.IsError)
        {
            // The transition was refused — a decline for an already-captured payment, say. The
            // attempt row still stands as evidence that it arrived, which is the point: a
            // provider telling us something impossible is worth being able to see.
            logger.LogWarning(
                "Payment {Reference} received a {Outcome} for {Operation} that its state machine "
                + "refused: {Error}. The attempt is recorded. Correlation {CorrelationId}.",
                payment.Reference.Value,
                result.Outcome,
                command.Operation,
                applied.FirstError.Description,
                result.CorrelationId);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            return applied.Errors;
        }

        // A payment that has reached a dead end releases its order's claim, so the customer can
        // try a different card. A captured one keeps it forever — see PaymentClaim.
        if (ShouldReleaseClaim(payment))
        {
            var claim = await payments.GetClaimAsync(payment.OrderId, cancellationToken);

            if (claim is not null)
            {
                payments.Remove(claim);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // The state as it now is, read off the aggregate this handler just mutated. The caller
        // must NOT re-read it — see PaymentOutcomeRecorded.
        return new PaymentOutcomeRecorded(
            payment.Status.ToString(),
            payment.DeclineCode,
            payment.CanSubmit,
            payment.IndeterminateSinceUtc is not null);
    }

    /// <summary>
    /// The four outcomes, mapped to four transitions. <b>No default that drifts to success.</b>
    /// </summary>
    private static ErrorOr<Success> Apply(
        Payment payment,
        PaymentOperation operation,
        GatewayResult result,
        DateTimeOffset nowUtc)
    {
        switch (result.Outcome)
        {
            case AttemptOutcome.Succeeded:
                // The reference can only come from the provider's answer, and a blank one is a
                // missing answer rather than an empty one. If a provider ever returns success
                // with no reference, that is a protocol violation and the payment does NOT settle
                // — which is M4 holding even against a misbehaving provider.
                var reference = ProviderReference.FromProvider(result.ProviderReference);

                if (reference.IsError)
                {
                    return reference.Errors;
                }

                return operation switch
                {
                    // Authorize-and-capture in one call is the common provider shape, so a
                    // successful authorization settles the money. A provider that separates them
                    // returns its capture through PaymentOperation.Capture.
                    PaymentOperation.Authorize or PaymentOperation.Capture =>
                        payment.Capture(reference.Value, nowUtc),

                    PaymentOperation.Enquire =>
                        // Reconciliation found the charge had in fact succeeded. The same
                        // transition — which is the point of asking.
                        payment.Capture(reference.Value, nowUtc),

                    _ => Error.Unexpected(
                        "Payment.UnexpectedOperation",
                        $"A successful {operation} is not applied here.")
                };

            case AttemptOutcome.Declined:
                return payment.Decline(result.ProviderCode, nowUtc);

            case AttemptOutcome.TransportFailure:
                // The provider never saw the request, so the outcome is known: nothing happened.
                return payment.Fail(outcomeIsUnknown: false, nowUtc);

            case AttemptOutcome.Indeterminate:
                // We sent it and never learned the result. NOT a failure in the business sense,
                // and the flag is what stops a consumer releasing the order's holds for a charge
                // that may have succeeded.
                return payment.Fail(outcomeIsUnknown: true, nowUtc);

            default:
                return Error.Unexpected(
                    "Payment.UnknownOutcome",
                    $"Outcome '{result.Outcome}' has no defined handling, so it is not treated as "
                    + "anything. Absence of a positive provider response is never success.");
        }
    }

    /// <summary>
    /// Whether the order should be free to be paid by something else.
    ///
    /// <para>
    /// A decline releases it immediately — the customer needs to try another card. An exhausted
    /// budget releases it only when the outcome is <i>known</i>: a payment that may have
    /// succeeded must keep its claim, or a second payment could charge the same order twice while
    /// reconciliation is still working out what happened to the first.
    /// </para>
    /// </summary>
    private static bool ShouldReleaseClaim(Payment payment) =>
        payment.Status.IsDeclined
            || (payment.Status.IsFailed
                && payment.SubmissionAttempts >= Payment.MaxAttempts
                && payment.IndeterminateSinceUtc is null);
}
