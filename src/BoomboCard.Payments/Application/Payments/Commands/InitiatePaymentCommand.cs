using BoomboCard.Payments.Contracts;
using BoomboCard.Payments.Data;
using BoomboCard.Payments.Data.Repositories;
using BoomboCard.Payments.Domain.PaymentAggregate;
using BoomboCard.Sales.Contracts;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Payments.Application.Payments.Commands;

/// <summary>What initiating a payment returns. No provider has been contacted.</summary>
public sealed record InitiatedPaymentResponse(
    Guid PaymentId,
    string Reference,
    decimal Amount,
    string CurrencyCode,
    string Status);

/// <summary>
/// Creates the payment intent. <b>TX A of two, and it contacts no provider.</b>
///
/// <para>
/// <b>This split is the whole of Onyx A12.</b> That controller opened a transaction, made up to
/// three gateway calls inside it at the default <c>HttpClient</c> timeout of 100 seconds each,
/// and then committed. On its single-writer SQLite database that blocked every write in the
/// entire system for up to five minutes per request — one slow payment provider was a complete
/// outage.
/// </para>
///
/// <para>
/// So the intent is committed first, on its own, in a transaction that touches nothing external.
/// The provider call happens in <c>SubmitPaymentCommand</c>, outside every transaction, and the
/// outcome is recorded in a third short one.
/// </para>
///
/// <para>
/// It also makes the idempotency key <b>durable before the first attempt</b>, which is the part
/// that is easy to miss: a key generated in memory and lost to a crash mid-attempt is not an
/// idempotency key at all, and the retry that follows is <b>M5</b> again.
/// </para>
/// </summary>
public sealed record InitiatePaymentCommand(Guid OrderId)
    : IRequest<ErrorOr<InitiatedPaymentResponse>>, IPaymentsRequest;

internal sealed class InitiatePaymentCommandHandler(
    IPaymentRepository payments,
    IUnitOfWork<PaymentsDbContext> unitOfWork,
    ISender sender,
    TimeProvider timeProvider)
    : IRequestHandler<InitiatePaymentCommand, ErrorOr<InitiatedPaymentResponse>>
{
    public async Task<ErrorOr<InitiatedPaymentResponse>> Handle(
        InitiatePaymentCommand command,
        CancellationToken cancellationToken)
    {
        // THE AMOUNT COMES FROM THE ORDER (M2).
        //
        // Not from the request — this command carries an order id and nothing else, so there is
        // no money value on the wire for anyone to tamper with. Onyx's order request carried the
        // points to redeem and computed its total from a basket the client described; here Sales
        // owns the figure and Payments asks for it.
        var order = await sender.Send(new GetOrderSummaryQuery(command.OrderId), cancellationToken);

        if (order.IsError)
        {
            return order.Errors;
        }

        var amount = Money.Create(order.Value.Total, order.Value.CurrencyCode);

        if (amount.IsError)
        {
            return amount.Errors;
        }

        // The claim is read first as the fast path; PaymentClaims' primary key is the guarantee.
        // Two simultaneous initiations both pass this read, the second insert is rejected, and
        // ICommandDispatcher retries — at which point the read sees the committed claim.
        var existing = await payments.GetClaimAsync(command.OrderId, cancellationToken);

        if (existing is not null)
        {
            return PaymentErrors.AlreadyInitiated(command.OrderId);
        }

        var nowUtc = timeProvider.GetUtcNow();

        var payment = Payment.Initiate(
            command.OrderId,
            order.Value.CustomerId,
            // The reference every hold was placed under, carried so a decline can release them.
            order.Value.CheckoutSessionId,
            amount.Value,
            nowUtc);

        if (payment.IsError)
        {
            return payment.Errors;
        }

        payments.Add(payment.Value);
        payments.Add(PaymentClaim.For(payment.Value, nowUtc));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new InitiatedPaymentResponse(
            payment.Value.Id,
            payment.Value.Reference.Value,
            payment.Value.Amount.Amount,
            payment.Value.Amount.CurrencyCode,
            payment.Value.Status.ToString());
    }
}
