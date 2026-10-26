using BoomboCard.Sales.Domain.CheckoutSessionAggregate;
using BoomboCard.Sales.Domain.Numbering;
using BoomboCard.Sales.Domain.OrderAggregate.Events;
using BoomboCard.Sales.Domain.Pricing;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Sales.Domain.OrderAggregate;

/// <summary>
/// A sale that happened: what was bought, what it cost and why, and where it is in its life.
///
/// Invariants it holds:
///  - An order can only be created from a <c>Confirmed</c> checkout session.
///  - The total equals the sum of its items and adjustments, copied from the session.
///  - Status transitions follow <see cref="OrderStatus"/>; <c>Paid</c> is only ever set by a
///    payment provider's confirmed capture.
///  - Nothing ships before the order is paid, and nothing cancels after it starts shipping.
///
/// <para>
/// <b>The single most important line in this class is the signature of
/// <see cref="PlaceFrom"/>:</b> it takes a <c>CheckoutSession</c>, and refuses one that is not
/// <c>Holding</c>. There is no other constructor. That is <b>M19</b> and <b>D2</b> made
/// structural — "an order is created only from a fully validated, fully priced session whose
/// stock is actually reserved" — rather than a rule a handler has to remember.
/// </para>
///
/// <para>
/// <c>Holding</c> rather than <c>Confirmed</c>, and the distinction took a bug to notice. The
/// first version demanded <c>Confirmed</c>, which forced the handler to confirm the session
/// before it had an order id to confirm it <i>with</i> — so it passed <c>Guid.Empty</c>, and
/// every <c>CheckoutConfirmedEvent</c> would have carried a meaningless id. The dependency runs
/// the other way: <c>Holding</c> means the stock is reserved and an order may be placed;
/// <c>Confirmed</c> is the <i>result</i> of placing one.
/// </para>
///
/// <para>
/// Onyx's <c>OrdersController.Create</c> did the opposite, in this order: created the order row,
/// saved it, then looped the requested lines deducting stock and calling
/// <c>SaveChangesAsync</c> <i>inside the loop</i>, returning <c>Ok(new { error = ... })</c> on
/// the first short line — leaving a committed order with some lines, stock gone for those lines,
/// and <c>Total = 0</c> because the total was computed after the loop. Then it computed the
/// total and saved again, then redeemed loyalty points in a separate transaction, then fired a
/// confirmation email into <c>Task.Run</c> on a method that threw. Seven findings in forty
/// lines: <b>D2</b>, <b>D3</b>, <b>D4</b>, <b>M19</b>, <b>A23</b>, <b>A8</b>, <b>D8</b>.
/// </para>
/// </summary>
public sealed class Order : AggregateRoot
{
    private readonly List<OrderItem> _items = [];
    private readonly List<OrderAdjustment> _adjustments = [];
    private readonly List<OrderPayment> _payments = [];

    public OrderNumber Number { get; private set; } = null!;

    public Guid CustomerId { get; private init; }

    /// <summary>
    /// The session this order came from. The correlation handle every hold in another module is
    /// keyed on, kept so a cancellation can release them by the same reference.
    /// </summary>
    public Guid CheckoutSessionId { get; private init; }

    public string CountryCode { get; private init; } = string.Empty;

    public OrderStatus Status { get; private set; } = OrderStatus.PaymentPending;

    public DateTimeOffset PlacedAtUtc { get; private init; }

    public DateTimeOffset? PaidAtUtc { get; private set; }

    public DateTimeOffset? ShippedAtUtc { get; private set; }

    public DateTimeOffset? DeliveredAtUtc { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public DateTimeOffset? RefundedAtUtc { get; private set; }

    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();

    public IReadOnlyCollection<OrderAdjustment> Adjustments => _adjustments.AsReadOnly();

    public Money MerchandiseSubtotal { get; private set; } = null!;

    public Money DiscountedMerchandise { get; private set; } = null!;

    public Money Shipping { get; private set; } = null!;

    public Money Tax { get; private set; } = null!;

    public Money Total { get; private set; } = null!;

    /// <summary>
    /// Merchandise after discounts, excluding tax and shipping — carried on the order so the
    /// loyalty earn uses the figure that was actually charged rather than recomputing it from
    /// parts. The definition lives in <c>QuoteCalculator.QualifyingSpend</c>.
    /// </summary>
    public Money QualifyingSpend { get; private set; } = null!;

    public Guid? PromotionId { get; private set; }

    public int PointsRedeemed { get; private set; }

    /// <summary>
    /// Every capture a provider confirmed against this order. See <see cref="OrderPayment"/> for
    /// why the reference is a row rather than a nullable column — in short, a nullable unique
    /// column cannot be indexed uniquely without provider-specific SQL, and the reference needs
    /// that index to be the idempotency guarantee rather than a hopeful read.
    /// </summary>
    public IReadOnlyCollection<OrderPayment> Payments => _payments.AsReadOnly();

    /// <summary>
    /// The provider's reference, set only from a capture. Never fabricated (<b>M4</b>).
    ///
    /// <para>
    /// Derived from <see cref="Payments"/> rather than stored beside it, so the reference and the
    /// row that carries the unique index cannot disagree. <c>Status.MarkPaid</c> permits exactly
    /// one capture today, so this is that one.
    /// </para>
    /// </summary>
    public string? PaymentReference =>
        _payments.Count == 0 ? null : _payments[0].Reference;

    private Order() { }

    /// <summary>
    /// Creates the order from a confirmed session, copying the quote verbatim.
    ///
    /// <para>
    /// <b>Copied, not recomputed.</b> Re-running the pipeline here would be a second place that
    /// prices a basket, and the two could disagree — which is Onyx <b>A6</b>'s shape ("browsing
    /// and buying read prices from different sources") moved one layer in. The session is the
    /// authority for what this costs; the order records what it cost.
    /// </para>
    /// </summary>
    public static ErrorOr<Order> PlaceFrom(
        CheckoutSession session,
        OrderNumber number,
        DateTimeOffset nowUtc)
    {
        // Holding means TX 1 succeeded: the stock, the promotion use and the points are reserved
        // against this session's id. An order for a session that never reserved its stock is the
        // state Onyx shipped in (D2).
        if (!session.Status.IsHolding)
        {
            return OrderErrors.SessionNotHolding;
        }

        // The guard that would have caught Onyx M19. A total that does not equal its rows means
        // something has gone wrong upstream, and refusing to create the order is strictly better
        // than creating one whose figures cannot be explained.
        if (!session.TotalMatchesItsRows())
        {
            return CheckoutErrors.TotalDoesNotMatchItsRows;
        }

        var order = new Order
        {
            Id = Guid.NewGuid(),
            Number = number,
            CustomerId = session.CustomerId,
            CheckoutSessionId = session.Id,
            CountryCode = session.CountryCode,
            Status = OrderStatus.PaymentPending,
            PlacedAtUtc = nowUtc,
            MerchandiseSubtotal = session.MerchandiseSubtotal,
            DiscountedMerchandise = session.DiscountedMerchandise,
            Shipping = session.Shipping,
            Tax = session.Tax,
            Total = session.Total,

            // A COPY, not the same instance. EF maps DiscountedMerchandise and QualifyingSpend
            // to two separate owned types, and an owned instance belongs to exactly one
            // navigation — sharing one between two makes the change tracker read the second
            // navigation's shadow key off the first and throw on Add. They are equal as values
            // (Money is a ValueObject), so nothing downstream can tell the difference.
            //
            // BUGS-AND-LESSONS section 12.
            QualifyingSpend = Copy(session.DiscountedMerchandise),
            PromotionId = session.PromotionId,
            PointsRedeemed = session.PointsRedeemed
        };

        foreach (var line in session.Lines)
        {
            order._items.Add(OrderItem.Create(
                order.Id,
                line.ProductId,
                line.Sku,
                line.Name,
                line.Quantity,
                line.UnitPrice,
                line.LineTotal,
                line.PriceId));
        }

        foreach (var adjustment in session.Adjustments)
        {
            order._adjustments.Add(OrderAdjustment.CopyFrom(order.Id, adjustment));
        }

        order.RaiseDomainEvent(new OrderPlacedEvent(
            order.Id,
            number.Value,
            order.CustomerId,
            session.Id,
            order.Total.Amount,
            order.Total.CurrencyCode,
            order.QualifyingSpend.Amount,
            nowUtc));

        return order;
    }

    // ---------------------------------------------------------------- the lifecycle

    /// <summary>
    /// Records that the provider captured the money.
    ///
    /// <para>
    /// <paramref name="paymentReference"/> is required and must be non-empty, because the only
    /// legitimate source of it is a provider response. Onyx <b>M4</b> fabricated one — when the
    /// gateway failed it substituted a locally generated six-digit number, so the reference
    /// looked valid and reconciliation was impossible. And <b>M1</b> set <c>Status = Paid</c>
    /// unconditionally after a loop that swallowed every exception, so a declined card produced a
    /// paid order.
    /// </para>
    /// </summary>
    public ErrorOr<Success> MarkPaid(string paymentReference, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(paymentReference))
        {
            return OrderErrors.PaymentReferenceRequired;
        }

        var status = Status.MarkPaid();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        PaidAtUtc = nowUtc;

        // The row IS the idempotency guarantee: UX_OrderPayments_Reference rejects a second
        // capture under the same provider reference even when two redeliveries race past the
        // handler's check.
        _payments.Add(OrderPayment.Capture(Id, paymentReference, Total, nowUtc));

        RaiseDomainEvent(new OrderPaidEvent(
            Id,
            Number.Value,
            CustomerId,
            Total.Amount,
            Total.CurrencyCode,
            QualifyingSpend.Amount,
            paymentReference,
            nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// Ships some or all of a line. Moves the order to <c>Fulfilling</c> while anything is
    /// outstanding and to <c>Shipped</c> once nothing is.
    /// </summary>
    public ErrorOr<Success> Ship(
        IReadOnlyList<(Guid OrderItemId, int Quantity)> shipments,
        DateTimeOffset nowUtc)
    {
        if (shipments.Count == 0)
        {
            return OrderErrors.NothingToShip;
        }

        // Validate everything before mutating anything, so a bad line cannot leave half a
        // shipment recorded. Onyx D2 is the same shape: its loop saved per line, so a short
        // line partway through left the earlier deductions committed.
        foreach (var (itemId, quantity) in shipments)
        {
            var item = _items.SingleOrDefault(candidate => candidate.Id == itemId);

            if (item is null)
            {
                return OrderErrors.ItemNotFound(itemId);
            }

            if (quantity <= 0 || quantity > item.QuantityOutstanding)
            {
                return Error.Validation(
                    "Order.ShipQuantityInvalid",
                    $"Cannot ship {quantity} of '{item.Sku}'; {item.QuantityOutstanding} outstanding.");
            }
        }

        foreach (var (itemId, quantity) in shipments)
        {
            var shipped = _items.Single(candidate => candidate.Id == itemId).Ship(quantity);

            if (shipped.IsError)
            {
                return shipped.Errors;
            }
        }

        var everythingShipped = _items.All(item => item.IsFullyShipped);

        var status = Status.Ship(everythingShipped);

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        if (everythingShipped)
        {
            ShippedAtUtc = nowUtc;
        }

        RaiseDomainEvent(new OrderShippedEvent(
            Id,
            Number.Value,
            CustomerId,
            everythingShipped,
            // The units in THIS movement, which is what makes the second parcel's notification
            // say something different from the first's.
            shipments.Sum(shipment => shipment.Quantity),
            nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> MarkDelivered(DateTimeOffset nowUtc)
    {
        var status = Status.MarkDelivered();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        DeliveredAtUtc = nowUtc;

        RaiseDomainEvent(new OrderDeliveredEvent(Id, Number.Value, CustomerId, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// Cancels the order and raises the event that puts the stock, the promotion use and the
    /// points back.
    /// </summary>
    public ErrorOr<Success> Cancel(string reason, DateTimeOffset nowUtc)
    {
        var heldInventory = Status.HoldsInventory;
        var wasPaid = Status.IsPaid;

        var status = Status.Cancel();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        CancelledAtUtc = nowUtc;

        RaiseDomainEvent(new OrderCancelledEvent(
            Id,
            Number.Value,
            CustomerId,
            CheckoutSessionId,
            heldInventory,
            wasPaid,
            reason,
            nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> MarkRefunded(string refundReference, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(refundReference))
        {
            return OrderErrors.RefundReferenceRequired;
        }

        var status = Status.MarkRefunded();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        RefundedAtUtc = nowUtc;

        RaiseDomainEvent(new OrderRefundedEvent(
            Id, Number.Value, CustomerId, Total.Amount, Total.CurrencyCode,
            refundReference, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// A fresh <c>Money</c> with the same value.
    ///
    /// <para>
    /// Needed wherever one figure is assigned to two owned navigations. See
    /// <c>QualifyingSpend</c>'s assignment in <see cref="PlaceFrom"/> for why, and
    /// <c>QuoteCalculator.Calculate</c> for the same guard one layer up.
    /// </para>
    /// </summary>
    private static Money Copy(Money money) =>
        Money.Create(money.Amount, money.CurrencyCode).Value;

    /// <summary>
    /// The invariant <c>SalesIntegrityCheckJob</c> asserts, as on the session: the stored total
    /// equals the sum of the rows that explain it.
    /// </summary>
    public bool TotalMatchesItsRows() =>
        _items.Sum(item => item.LineTotal.Amount)
            + _adjustments.Sum(adjustment => adjustment.Amount) == Total.Amount;
}

/// <summary>
/// A quote adjustment, copied onto the order at the sale.
///
/// <para>
/// Copied rather than referenced, so the order is self-contained: a receipt reprinted in two
/// years must not depend on a checkout session row still existing, and the session rows are
/// swept. The duplication is deliberate and one-directional — nothing ever writes back.
/// </para>
/// </summary>
public sealed class OrderAdjustment : Entity
{
    public Guid OrderId { get; private init; }

    public QuoteAdjustmentKind Kind { get; private init; }

    public string Description { get; private init; } = string.Empty;

    public decimal Amount { get; private init; }

    public decimal BasisAmount { get; private init; }

    public Guid? PromotionId { get; private init; }

    public int? PointsRedeemed { get; private init; }

    private OrderAdjustment() { }

    internal static OrderAdjustment CopyFrom(Guid orderId, QuoteAdjustment adjustment) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Kind = adjustment.Kind,
            Description = adjustment.Description,
            Amount = adjustment.Amount,
            BasisAmount = adjustment.BasisAmount,
            PromotionId = adjustment.PromotionId,
            PointsRedeemed = adjustment.PointsRedeemed
        };
}

public static class OrderErrors
{
    public static Error NotFound(Guid orderId) =>
        Error.NotFound("Order.NotFound", $"Order '{orderId}' was not found.");

    public static Error NotFoundByNumber(string number) =>
        Error.NotFound("Order.NotFound", $"Order '{number}' was not found.");

    public static Error SessionNotHolding =>
        Error.Conflict(
            "Order.SessionNotHolding",
            "An order can only be created from a checkout session that has reserved its stock.");

    public static Error PaymentReferenceRequired =>
        Error.Validation(
            "Order.PaymentReferenceRequired",
            "An order can only be marked paid with a reference from the payment provider.");

    public static Error RefundReferenceRequired =>
        Error.Validation(
            "Order.RefundReferenceRequired",
            "A refund must name the provider reference that performed it.");

    public static Error NothingToShip =>
        Error.Validation("Order.NothingToShip", "A shipment must name at least one item.");

    public static Error ItemNotFound(Guid orderItemId) =>
        Error.NotFound("Order.ItemNotFound", $"Order item '{orderItemId}' was not found.");
}
