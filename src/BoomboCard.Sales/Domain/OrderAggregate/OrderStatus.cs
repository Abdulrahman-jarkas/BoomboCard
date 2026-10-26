using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Sales.Domain.OrderAggregate;

public enum OrderStatusValue
{
    /// <summary>
    /// The order exists and the stock is held, but nothing has been charged. What a confirmed
    /// checkout produces, and what the <c>202 Accepted</c> response describes.
    /// </summary>
    PaymentPending = 0,

    /// <summary>The provider has confirmed a capture. Set by a payment event and nothing else.</summary>
    Paid = 1,

    /// <summary>Some items have shipped, not all. Partial shipment.</summary>
    Fulfilling = 2,

    /// <summary>Every item has shipped.</summary>
    Shipped = 3,

    /// <summary>Confirmed delivered. Terminal for the happy path.</summary>
    Delivered = 4,

    /// <summary>Cancelled before fulfilment. Stock and holds returned. Terminal.</summary>
    Cancelled = 5,

    /// <summary>Money returned. Terminal.</summary>
    Refunded = 6
}

/// <summary>
/// The order lifecycle, with the legal transitions in the value object rather than scattered
/// through handlers.
///
/// <para>
/// <b>Onyx A31: "the order lifecycle stops at Paid".</b> Its <c>OrderStatus</c> had four values —
/// <c>Pending, Paid, Shipped, Cancelled</c> — and exactly one assignment anywhere in the
/// codebase: <c>order.Status = OrderStatus.Paid</c>, written unconditionally after a payment
/// loop that swallowed every exception (<b>M1</b>). Nothing ever set <c>Shipped</c> or
/// <c>Cancelled</c>, so two of the four states were unreachable, and there was no cancellation,
/// no refund, no fulfilment and no delivery (<c>ISSUES.md</c> §7).
/// </para>
///
/// <para>
/// <b>Why Paid cannot be reached from here by Sales.</b> <see cref="MarkPaid"/> exists, but the
/// only thing that calls it is a handler reacting to a payment provider's confirmed capture —
/// which is <b>M1</b>'s obligation: "the payment aggregate transitions on the provider's answer
/// and on nothing else". Sales never decides that money arrived.
/// </para>
/// </summary>
public sealed class OrderStatus : ValueObject
{
    public OrderStatusValue Value { get; }

    private OrderStatus(OrderStatusValue value) => Value = value;

    public static OrderStatus PaymentPending => new(OrderStatusValue.PaymentPending);

    public static OrderStatus From(OrderStatusValue value) => new(value);

    public bool IsPaymentPending => Value is OrderStatusValue.PaymentPending;

    public bool IsPaid => Value is OrderStatusValue.Paid;

    public bool IsCancelled => Value is OrderStatusValue.Cancelled;

    public bool IsRefunded => Value is OrderStatusValue.Refunded;

    /// <summary>Whether the stock and holds for this order are still committed to it.</summary>
    public bool HoldsInventory =>
        Value is OrderStatusValue.PaymentPending
            or OrderStatusValue.Paid
            or OrderStatusValue.Fulfilling;

    public ErrorOr<OrderStatus> MarkPaid() =>
        Value switch
        {
            OrderStatusValue.PaymentPending => new OrderStatus(OrderStatusValue.Paid),
            OrderStatusValue.Paid => OrderStatusErrors.AlreadyPaid,
            _ => OrderStatusErrors.NotPayable
        };

    /// <summary>
    /// Moves to <c>Fulfilling</c> or <c>Shipped</c> depending on whether everything has gone.
    /// The caller passes the answer because only the aggregate can see all the items.
    /// </summary>
    public ErrorOr<OrderStatus> Ship(bool everythingShipped) =>
        Value switch
        {
            OrderStatusValue.Paid or OrderStatusValue.Fulfilling => new OrderStatus(
                everythingShipped ? OrderStatusValue.Shipped : OrderStatusValue.Fulfilling),
            OrderStatusValue.PaymentPending => OrderStatusErrors.NotPaid,
            _ => OrderStatusErrors.NotFulfillable
        };

    public ErrorOr<OrderStatus> MarkDelivered() =>
        Value switch
        {
            OrderStatusValue.Shipped => new OrderStatus(OrderStatusValue.Delivered),
            _ => OrderStatusErrors.NotShipped
        };

    /// <summary>
    /// Cancellation is allowed before anything ships, and that is the line.
    ///
    /// Once an item is in a courier's hands, "cancel" is not a state change — it is a return,
    /// which is a different process with a different money flow. Pretending otherwise is how an
    /// order ends up Cancelled with a parcel still arriving.
    /// </summary>
    public ErrorOr<OrderStatus> Cancel() =>
        Value switch
        {
            OrderStatusValue.PaymentPending or OrderStatusValue.Paid =>
                new OrderStatus(OrderStatusValue.Cancelled),
            OrderStatusValue.Cancelled => OrderStatusErrors.AlreadyCancelled,
            _ => OrderStatusErrors.TooLateToCancel
        };

    public ErrorOr<OrderStatus> MarkRefunded() =>
        Value switch
        {
            OrderStatusValue.Refunded => OrderStatusErrors.AlreadyRefunded,
            OrderStatusValue.PaymentPending => OrderStatusErrors.NotPaid,
            _ => new OrderStatus(OrderStatusValue.Refunded)
        };

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString();
}

public static class OrderStatusErrors
{
    public static Error AlreadyPaid =>
        Error.Conflict("Order.AlreadyPaid", "This order has already been paid.");

    public static Error NotPayable =>
        Error.Conflict(
            "Order.NotPayable",
            "This order is cancelled or already fulfilled and cannot be paid.");

    public static Error NotPaid =>
        Error.Conflict("Order.NotPaid", "This order has not been paid yet.");

    public static Error NotFulfillable =>
        Error.Conflict("Order.NotFulfillable", "This order cannot be shipped in its current state.");

    public static Error NotShipped =>
        Error.Conflict("Order.NotShipped", "Only a fully shipped order can be marked delivered.");

    public static Error AlreadyCancelled =>
        Error.Conflict("Order.AlreadyCancelled", "This order is already cancelled.");

    public static Error TooLateToCancel =>
        Error.Conflict(
            "Order.TooLateToCancel",
            "This order has already started shipping. Request a return instead.");

    public static Error AlreadyRefunded =>
        Error.Conflict("Order.AlreadyRefunded", "This order has already been refunded.");
}
