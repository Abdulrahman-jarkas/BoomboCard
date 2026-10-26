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
/// What a submission returns. <c>Status</c> is the real status, whatever it is — a decline is a
/// 200 with <c>Declined</c>, not a 500.
/// </summary>
public sealed record SubmittedPaymentResponse(
    Guid PaymentId,
    string Reference,
    string Status,
    string? DeclineCode,
    bool CanRetry,
    string? Message);

/// <summary>
/// Sends the payment to the provider and records what came back.
///
/// <para>
/// <b>Deliberately NOT an <see cref="IPaymentsRequest"/>.</b> Every marked command in this
/// system gets a transaction from its module's pipeline behaviour, and this one must not have
/// one, because it makes an HTTP call. Onyx <b>A12</b> held a transaction across up to three
/// gateway calls at a 100-second timeout each; on its single-writer database that was up to five
/// minutes of total write unavailability per payment.
/// </para>
///
/// <para>
/// <c>ModuleWiringTests</c> asserts that every <see cref="ITransactionalRequest"/> carries exactly
/// one module marker — so the absence of the marker here is a visible decision rather than an
/// oversight that silently loses atomicity. The comment is the explanation; the test is what
/// stops someone "fixing" it.
/// </para>
///
/// <para>
/// <b>Three steps, and the ordering is the design:</b>
/// </para>
///
/// <list type="number">
/// <item><b>Read</b> the payment and check it may be submitted. No transaction.</item>
/// <item><b>Call the provider</b>, outside every transaction, with the payment's stable
/// idempotency key.</item>
/// <item><b>Record the outcome</b> by dispatching <see cref="RecordPaymentOutcomeCommand"/>,
/// which <i>is</i> marked and so gets its own short transaction.</item>
/// </list>
///
/// <para>
/// Step 3 is a separate command rather than inline code for a reason that matters: if recording
/// the outcome fails, the retrying <c>ICommandDispatcher</c> reloads the aggregate in a fresh
/// scope and tries again — <b>without re-calling the provider</b>. The expensive, non-idempotent
/// half and the cheap, retryable half are deliberately on opposite sides of the boundary.
/// </para>
/// </summary>
public sealed record SubmitPaymentCommand(Guid PaymentId, PaymentInstrument Instrument)
    : IRequest<ErrorOr<SubmittedPaymentResponse>>;

internal sealed class SubmitPaymentCommandHandler(
    IPaymentRepository payments,
    IPaymentGateway gateway,
    ICommandDispatcher dispatcher,
    ILogger<SubmitPaymentCommandHandler> logger)
    : IRequestHandler<SubmitPaymentCommand, ErrorOr<SubmittedPaymentResponse>>
{
    public async Task<ErrorOr<SubmittedPaymentResponse>> Handle(
        SubmitPaymentCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Instrument.Token))
        {
            return Error.Validation(
                "Payment.InstrumentRequired",
                "A provider token is required. Card details never reach this system.");
        }

        // ---- step 1: read. No transaction, because none is needed for a read.
        var payment = await payments.GetAsync(command.PaymentId, cancellationToken);

        if (payment is null)
        {
            return PaymentErrors.NotFound(command.PaymentId);
        }

        // Already answered. Returning the existing outcome rather than an error is what makes a
        // double-clicked Pay button harmless — M3 at the application layer, with PaymentClaims'
        // primary key behind it.
        if (!payment.Status.CanSubmit)
        {
            // Safe to describe the loaded aggregate here: nothing has been called and nothing has
            // changed it, so this context's view IS the current state.
            return new SubmittedPaymentResponse(
                payment.Id,
                payment.Reference.Value,
                payment.Status.ToString(),
                payment.DeclineCode,
                payment.CanSubmit,
                "This payment has already been answered.");
        }

        if (!payment.CanSubmit)
        {
            return PaymentErrors.AttemptsExhausted;
        }

        // ---- step 2: the provider call, OUTSIDE every transaction.
        //
        // The idempotency key is the payment's, generated once at initiation and sent on every
        // attempt — M5. Retry on transient transport failure happens inside the adapter's
        // resilience pipeline, which is safe precisely because this key goes with it.
        var result = await gateway.AuthorizeAsync(
            new GatewayAuthorizeRequest(
                payment.Id,
                payment.IdempotencyKey.Value,
                payment.Amount.Amount,
                payment.Amount.CurrencyCode,
                command.Instrument,
                $"Order {payment.OrderId}"),
            cancellationToken);

        // Logged at the level the outcome deserves, with the correlation id, before anything is
        // written. Onyx A16's `catch { }` destroyed all of this; the only trace of a payment
        // failure in that system was a Console.WriteLine no pipeline captured.
        LogOutcome(payment, result);

        // ---- step 3: record it, in its own short transaction.
        //
        // A separate dispatched command, so a failure here is retried WITHOUT re-calling the
        // provider. The aggregate is reloaded in a fresh scope by ICommandDispatcher, which is
        // the only safe way to retry after a concurrency conflict.
        var recorded = await dispatcher.SendAsync(
            new RecordPaymentOutcomeCommand(payment.Id, PaymentOperation.Authorize, result),
            cancellationToken);

        if (recorded.IsError)
        {
            // The provider may well have taken the money and we failed to write it down. That is
            // the worst state this module can be in, so it is logged as an error and left for
            // reconciliation — which asks the provider by idempotency key rather than guessing.
            logger.LogError(
                "Payment {Reference} got a {Outcome} from the provider and the outcome could not "
                + "be recorded: {Error}. Correlation {CorrelationId}. Reconciliation will resolve "
                + "it from the provider's own record.",
                payment.Reference.Value,
                result.Outcome,
                recorded.FirstError.Description,
                result.CorrelationId);

            return recorded.Errors;
        }

        // The state comes back FROM the command that changed it.
        //
        // Re-reading `payments.GetAsync(payment.Id)` here was a real bug: this handler's
        // DbContext has the payment tracked from step 1, EF's identity map returns that tracked
        // instance, and the write happened in a different scope opened by ICommandDispatcher. So
        // the response said "Pending" for a payment that was in fact "Captured" — a correct write
        // with an answer describing the state before it. BUGS-AND-LESSONS section 14.
        return new SubmittedPaymentResponse(
            payment.Id,
            payment.Reference.Value,
            recorded.Value.Status,
            recorded.Value.DeclineCode,
            recorded.Value.CanSubmit,
            MessageFor(result));
    }

    private void LogOutcome(Payment payment, GatewayResult result)
    {
        switch (result.Outcome)
        {
            case AttemptOutcome.Succeeded:
                logger.LogInformation(
                    "Payment {Reference} authorized by the provider in {LatencyMs}ms. "
                    + "Correlation {CorrelationId}.",
                    payment.Reference.Value,
                    result.LatencyMs,
                    result.CorrelationId);
                break;

            case AttemptOutcome.Declined:
                // Information, not an error: a decline is a normal business outcome and the most
                // common non-success one. Logging it as an error trains people to ignore errors.
                logger.LogInformation(
                    "Payment {Reference} was declined with {ProviderCode}. "
                    + "Correlation {CorrelationId}.",
                    payment.Reference.Value,
                    result.ProviderCode ?? "no code",
                    result.CorrelationId);
                break;

            case AttemptOutcome.Indeterminate:
                // The one that needs a human eventually. The charge may have succeeded.
                logger.LogError(
                    "Payment {Reference} got NO ANSWER from the provider after {LatencyMs}ms, so "
                    + "the card may or may not have been charged. Correlation {CorrelationId}. "
                    + "Detail: {Detail}. Reconciliation will ask the provider what happened to "
                    + "idempotency key {IdempotencyKey}.",
                    payment.Reference.Value,
                    result.LatencyMs,
                    result.CorrelationId,
                    result.Detail,
                    payment.IdempotencyKey.Value);
                break;

            default:
                logger.LogWarning(
                    "Payment {Reference} failed in transport after {LatencyMs}ms, so the provider "
                    + "never saw it. Correlation {CorrelationId}. Detail: {Detail}.",
                    payment.Reference.Value,
                    result.LatencyMs,
                    result.CorrelationId,
                    result.Detail);
                break;
        }
    }

    /// <summary>
    /// What to tell the customer. Deliberately vague about transport detail and specific about
    /// declines, because only one of those is actionable by them.
    /// </summary>
    private static string MessageFor(GatewayResult result) =>
        result.Outcome switch
        {
            AttemptOutcome.Succeeded => "Payment accepted.",
            AttemptOutcome.Declined => result.ProviderCode switch
            {
                "insufficient_funds" => "The card was declined for insufficient funds. Try another card.",
                "expired_card" => "The card has expired. Check the expiry date, or try another card.",
                "do_not_honor" => "The bank declined the payment. Contact them, or try another card.",
                _ => "The card was declined. Try another card."
            },
            AttemptOutcome.Indeterminate =>
                "We did not get a confirmation from the payment provider. Do not pay again — we "
                + "are checking with them and will confirm shortly.",
            _ => "We could not reach the payment provider. Please try again in a moment."
        };

}
