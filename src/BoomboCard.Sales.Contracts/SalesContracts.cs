using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Sales.Contracts;

/// <summary>
/// Marks a request as a Sales command: it mutates Sales state, and so must run inside a
/// transaction on Sales' context.
///
/// See <see cref="IUnitOfWork{TScope}"/> for why the module is carried by the request type.
/// </summary>
public interface ISalesRequest : ITransactionalRequest;

/// <summary>
/// Tells Sales a payment provider captured the money for an order.
///
/// <para>
/// <b>Sales never decides that money arrived</b> — this command is the only route to
/// <c>Order.MarkPaid</c>, and <c>PaymentReference</c> is required. That is <b>M1</b>'s
/// obligation ("the payment aggregate transitions on the provider's answer and on nothing
/// else") and <b>M4</b>'s ("the provider reference is nullable and set only from a provider
/// response") enforced from this side of the boundary as well.
/// </para>
///
/// <para>
/// <b>Its caller arrived in Phase 5, and this contract needed no change.</b> When it was written
/// its own documentation predicted the shape: <i>"Published as a command rather than consumed as
/// an event because Payments does not exist yet. When it does, the shape becomes Sales
/// subscribing to its <c>PaymentCapturedIntegrationEvent</c> through the inbox and calling
/// this."</i> That is exactly what <c>OnPaymentCapturedMarkOrderPaid</c> does — the command, its
/// idempotency on the provider reference and the state machine behind it were all already here.
/// </para>
///
/// <para>
/// <b>It still has no HTTP route</b>, and that absence is now load-bearing rather than
/// provisional: the only caller is that consumer, and the only thing that can make the consumer
/// run is a payment provider having said the money moved. Idempotent on
/// <c>PaymentReference</c>, backed by <c>UX_OrderPayments_Reference</c>, so a redelivered capture
/// marks the order paid once.
/// </para>
/// </summary>
public sealed record MarkOrderPaidCommand(Guid OrderId, string PaymentReference)
    : IRequest<ErrorOr<Success>>, ISalesRequest;

/// <summary>
/// Cancels an order, releasing the stock, the promotion use and the points it held.
/// </summary>
public sealed record CancelOrderCommand(Guid OrderId, string Reason)
    : IRequest<ErrorOr<Success>>, ISalesRequest;

/// <summary>
/// Records a refund against an order, which reverses the loyalty points it earned.
/// </summary>
public sealed record RefundOrderCommand(Guid OrderId, string RefundReference)
    : IRequest<ErrorOr<Success>>, ISalesRequest;

/// <summary>
/// The minimum another module needs to know about an order: who, how much, and where it is.
///
/// <para>
/// <c>CheckoutSessionId</c> is the reference every hold in every module was placed under. Added
/// in Phase 5 for one consumer: a declined payment has to release those holds, and the session id
/// is the only handle to them. Widening this contract rather than publishing a second query,
/// because it is a fact about the order and the order is what this record describes.
/// </para>
/// </summary>
public sealed record OrderSummaryResponse(
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    string Status,
    decimal Total,
    string CurrencyCode,
    DateTimeOffset PlacedAtUtc,
    Guid CheckoutSessionId);

/// <summary>A query, so no transaction.</summary>
public sealed record GetOrderSummaryQuery(Guid OrderId)
    : IRequest<ErrorOr<OrderSummaryResponse>>;

/// <summary>
/// Whether a checkout session reached <c>Confirmed</c>.
///
/// <para>
/// Published for <b>recovery layer 3</b>, the orphan-sale reconciliation. Catalog can list stock
/// holds older than the session lifetime but cannot tell a completed sale from an abandoned one,
/// because the reference is a session id that Sales owns — which is exactly why the README
/// recorded that sweep as deferred until Sales existed. This query is the missing half.
/// </para>
/// </summary>
public sealed record CheckoutSessionOutcome(
    string Reference,
    bool Exists,
    bool IsConfirmed,
    bool IsLive);

public sealed record GetCheckoutOutcomesQuery(IReadOnlyList<string> References)
    : IRequest<ErrorOr<List<CheckoutSessionOutcome>>>;
