using BoomboCard.SharedKernel;

namespace BoomboCard.Sales.Contracts.IntegrationEvents;

/*
 * Sales' cross-boundary facts.
 *
 * These are the events the rest of the system has been waiting for. Loyalty's earn rule, built
 * and tested in Phase 3, had no trigger because OrderPaid did not exist; Notifications (Phase 6)
 * has nothing to tell anybody about until an order is placed.
 */

/// <summary>
/// An order exists, payment is pending. The <c>202 Accepted</c> a confirmed checkout returns.
///
/// <para>
/// Notifications sends the order confirmation from this. Note what it is <b>not</b>: a
/// notification that the order is paid. Onyx <b>A9</b> dispatched its payment notification
/// before the payment row was even added to the context, so a customer could be told their
/// payment had succeeded for a transaction that then rolled back.
/// </para>
/// </summary>
public sealed record OrderPlacedIntegrationEvent(
    Guid EventId,
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    decimal Total,
    string CurrencyCode,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// A payment provider captured the money.
///
/// <para>
/// <b>This is the event Loyalty's earn rule has been waiting for since Phase 3.</b>
/// <paramref name="QualifyingSpend"/> is merchandise after discounts, excluding tax and shipping
/// — computed once by <c>QuoteCalculator.QualifyingSpend</c> and carried on the order, so the
/// earn uses the figure that was actually charged rather than re-deriving it from parts. Onyx
/// <b>M12</b>: nothing anywhere increased the points balance, so the scheme had a spend side and
/// no earn side.
/// </para>
///
/// <para>
/// Paid rather than placed is the trigger, deliberately: points are a reward for money received,
/// and earning on an unpaid order means a card that later declines has already paid out.
/// <b>M12</b>'s obligation line says "triggered by a defined event (order completed, or
/// delivered)" — this picks the earliest point at which the money is real.
/// </para>
/// </summary>
public sealed record OrderPaidIntegrationEvent(
    Guid EventId,
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    decimal Total,
    decimal QualifyingSpend,
    string CurrencyCode,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// Some or all of an order has gone out.
///
/// <para>
/// <b>Added in Phase 6, and the gap is worth naming.</b> <c>OrderShippedEvent</c> and
/// <c>OrderDeliveredEvent</c> have existed as <i>domain</i> events since Phase 4, raised by a
/// guarded state machine with per-item quantities — and nothing published them, because nothing
/// outside Sales had asked. Onyx <b>A31</b> is that its order lifecycle "stops at Paid" and
/// nothing ever set <c>Shipped</c> at all; BoomboCard could set it and could not tell anybody.
/// </para>
///
/// <para>
/// <paramref name="PartialShipment"/> is the field the notification turns on. A two-line order
/// can go out in two parcels, which Onyx's single order-level status string could not represent —
/// and a customer who receives one box after being told the order shipped needs to know whether
/// to expect another.
/// </para>
/// </summary>
public sealed record OrderShippedIntegrationEvent(
    Guid EventId,
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    int ItemsShipped,
    bool PartialShipment,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>It arrived. The end of the lifecycle Onyx <b>A31</b> never reached.</summary>
public sealed record OrderDeliveredIntegrationEvent(
    Guid EventId,
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// An order was cancelled. Catalog restores the stock, Promotions releases the use, Loyalty
/// returns the points — all keyed on <paramref name="CheckoutSessionId"/>, which is the one
/// reference every hold in the system was placed under.
/// </summary>
public sealed record OrderCancelledIntegrationEvent(
    Guid EventId,
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    Guid CheckoutSessionId,
    string Reason,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// An order was refunded. Loyalty reverses the points it earned, bounded by what the earning
/// actually gave — <c>ISSUES.md</c> §7 lists "reversal on refund" as a missing capability and
/// <b>M12</b>'s obligation line names it.
/// </summary>
public sealed record OrderRefundedIntegrationEvent(
    Guid EventId,
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    decimal Amount,
    string CurrencyCode,
    string RefundReference,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// A checkout session's window passed without it being confirmed.
///
/// <para>
/// <b>Recovery layer 2, and the layer the saga actually relies on.</b> Inline compensation can
/// fail for the same reason the step it is compensating for failed; a sweeper that publishes
/// this, and consumers that release unconditionally and idempotently, cannot. Every consumer
/// treats "nothing was held" as success.
/// </para>
/// </summary>
public sealed record CheckoutAbandonedIntegrationEvent(
    Guid EventId,
    Guid CheckoutSessionId,
    Guid CustomerId,
    string Reason,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
