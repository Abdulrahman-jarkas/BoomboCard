using BoomboCard.SharedKernel;

namespace BoomboCard.Sales.Domain.OrderAggregate.Events;

/*
 * The order's facts — one per state transition, which is what makes the lifecycle auditable.
 *
 * Onyx A40 is "no audit trail of any kind", and A31 is a lifecycle with two unreachable states
 * and one unconditional assignment. Between them, nothing could reconstruct what had happened to
 * an order or when. Every transition here raises an event carrying the ids and the instant, so
 * the history is a consequence of the design rather than something that has to be remembered.
 */

public sealed record OrderPlacedEvent(
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    Guid CheckoutSessionId,
    decimal Total,
    string CurrencyCode,
    decimal QualifyingSpend,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Raised only by a handler reacting to a payment provider's confirmed capture — never by Sales
/// deciding money arrived. <b>M1</b>: "the payment aggregate transitions on the provider's
/// answer and on nothing else."
/// </summary>
public sealed record OrderPaidEvent(
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    decimal Total,
    string CurrencyCode,
    decimal QualifyingSpend,
    string PaymentReference,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Some or all of an order has gone out.
///
/// <para>
/// <paramref name="UnitsShipped"/> was added in Phase 6, for the notification: "order BC-00000123
/// has shipped" is a weaker message than "3 items have shipped", and a partial shipment is
/// confusing without it. It is the count in <i>this</i> movement, not the order's total — which
/// is what makes the second parcel's email say something different from the first's.
/// </para>
/// </summary>
public sealed record OrderShippedEvent(
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    bool FullyShipped,
    int UnitsShipped,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record OrderDeliveredEvent(
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Carries <paramref name="HeldInventory"/> so the handler knows whether stock has to go back —
/// though the release fires unconditionally regardless, because idempotent restores are what
/// make the recovery layers reliable rather than clever.
/// </summary>
public sealed record OrderCancelledEvent(
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    Guid CheckoutSessionId,
    bool HeldInventory,
    bool WasPaid,
    string Reason,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record OrderRefundedEvent(
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    decimal Amount,
    string CurrencyCode,
    string RefundReference,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
