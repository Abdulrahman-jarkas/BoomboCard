using BoomboCard.Sales.Domain.CheckoutSessionAggregate;
using BoomboCard.Sales.Domain.Numbering;
using BoomboCard.Sales.Domain.OrderAggregate;
using BoomboCard.Sales.Domain.Pricing;
using BoomboCard.SharedKernel;

namespace BoomboCard.Sales.Tests;

/// <summary>
/// The quote aggregate and the order state machine.
///
/// <para>
/// The register's §7 lists "an order state machine" as a capability Onyx did not have. It had a
/// <c>string Status</c> that <c>PUT /api/orders/{id}/status</c> set to any value a caller sent
/// (<b>S7</b>), nothing ever assigned <c>Shipped</c>, and partial shipment could not be
/// represented at all. These are the tests that make a status mean something.
/// </para>
/// </summary>
public class CheckoutSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Country Us = Country.UnitedStates;
    private static readonly ShippingPolicy UsShipping = ShippingPolicy.UnitedStates;

    private static CheckoutSession Session(
        decimal unitPrice = 50.00m,
        int quantity = 1,
        PromotionInput? promotion = null,
        LoyaltyInput? loyalty = null)
    {
        var started = CheckoutSession.Start(
            Guid.NewGuid(),
            Us,
            UsShipping,
            [new QuotedLine(Guid.NewGuid(), "SKU-1", "A thing", quantity, unitPrice, Guid.NewGuid())],
            promotion,
            loyalty,
            Now);

        Assert.False(started.IsError, started.IsError ? started.FirstError.Description : string.Empty);

        return started.Value;
    }

    private static Order PlacedOrder(CheckoutSession? session = null)
    {
        session ??= Session();
        Assert.False(session.BeginHolding(Now).IsError);

        var order = Order.PlaceFrom(session, OrderNumber.FromSequence(1), Now);

        Assert.False(order.IsError, order.IsError ? order.FirstError.Description : string.Empty);
        Assert.False(session.Confirm(order.Value.Id, Now).IsError);

        return order.Value;
    }

    // ------------------------------------------------------------------ the quote

    /// <summary>
    /// A session always has a total, and that total always equals its rows.
    ///
    /// <para>
    /// Priced at creation rather than lazily, because an unpriced session can be confirmed
    /// before anybody has computed what it costs — <b>M19</b> exactly.
    /// </para>
    /// </summary>
    [Fact]
    public void ASessionIsPricedAtCreationAndItsTotalMatchesItsRows()
    {
        var session = Session(unitPrice: 50.00m, quantity: 2);

        Assert.Equal(100.00m, session.MerchandiseSubtotal.Amount);
        Assert.Equal(0m, session.Shipping.Amount);          // 100 >= 75
        Assert.Equal(108.00m, session.Total.Amount);        // + 8% tax
        Assert.True(session.TotalMatchesItsRows());
    }

    /// <summary>
    /// The adjustment rows are what make a total explicable. Onyx stored one <c>decimal</c>
    /// computed by a single expression with four interleaved rules, so nothing could say which
    /// rule produced which part of it.
    /// </summary>
    [Fact]
    public void EveryPricingStepLeavesAnAdjustmentRowCarryingItsBasis()
    {
        var session = Session(
            unitPrice: 100.00m,
            promotion: new PromotionInput(Guid.NewGuid(), "SAVE20", 20.00m),
            loyalty: new LoyaltyInput(1_000, 10.00m));

        var kinds = session.Adjustments.Select(adjustment => adjustment.Kind).ToList();

        Assert.Contains(QuoteAdjustmentKind.PromotionDiscount, kinds);
        Assert.Contains(QuoteAdjustmentKind.LoyaltyDiscount, kinds);
        Assert.Contains(QuoteAdjustmentKind.Tax, kinds);

        // Every row names the figure it was computed from, which is what makes "why is my tax
        // $5.60?" answerable from the response rather than from the source.
        Assert.All(session.Adjustments, adjustment => Assert.True(adjustment.BasisAmount >= 0m));

        Assert.True(session.TotalMatchesItsRows());
    }

    /// <summary>
    /// The quoted price and its <c>PriceId</c> are captured on the line.
    ///
    /// <para>
    /// The id is what lets the system <i>prove</i> which price the customer was shown, rather
    /// than asserting it. Onyx <b>A6</b>: browsing read a never-invalidated cache and ordering
    /// read the live row, so after any price change a shopper saw one price and was charged
    /// another — and nobody found out, because the confirmation emails silently failed.
    /// </para>
    /// </summary>
    [Fact]
    public void EachLineCapturesThePriceIdItWasQuotedAt()
    {
        var priceId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        var session = CheckoutSession.Start(
            Guid.NewGuid(),
            Us,
            UsShipping,
            [new QuotedLine(productId, "SKU-1", "A thing", 3, 10.00m, priceId)],
            promotion: null,
            loyalty: null,
            Now).Value;

        var line = Assert.Single(session.Lines);

        Assert.Equal(priceId, line.PriceId);
        Assert.Equal(10.00m, line.UnitPrice.Amount);
        Assert.Equal(30.00m, line.LineTotal.Amount);
    }

    [Fact]
    public void ASessionWithNoLinesIsRefused()
    {
        var result = CheckoutSession.Start(
            Guid.NewGuid(), Us, UsShipping, [], null, null, Now);

        Assert.True(result.IsError);
        Assert.Equal(CheckoutErrors.NoLines.Code, result.FirstError.Code);
    }

    /// <summary>
    /// The line cap is a bound on work a single request can cause. Onyx <b>A28</b> left every
    /// such parameter unvalidated.
    /// </summary>
    [Fact]
    public void ASessionBeyondTheLineCapIsRefused()
    {
        var lines = Enumerable.Range(0, CheckoutSession.MaxLines + 1)
            .Select(index => new QuotedLine(
                Guid.NewGuid(), $"SKU-{index}", "A thing", 1, 1.00m, Guid.NewGuid()))
            .ToList();

        var result = CheckoutSession.Start(
            Guid.NewGuid(), Us, UsShipping, lines, null, null, Now);

        Assert.True(result.IsError);
    }

    // ------------------------------------------------------------------ the deadline

    /// <summary>
    /// The deadline is what bounds the system's exposure. Without it a checkout that neither
    /// succeeds nor fails holds stock forever — Onyx <b>D8</b>.
    /// </summary>
    [Fact]
    public void ASessionExpiresAfterItsLifetime()
    {
        var session = Session();

        Assert.Equal(Now + CheckoutSession.Lifetime, session.ExpiresAtUtc);
        Assert.False(session.HasExpiredAt(session.ExpiresAtUtc.AddTicks(-1)));
        Assert.True(session.HasExpiredAt(session.ExpiresAtUtc));
    }

    /// <summary>
    /// Re-reading a quote does not extend it, and neither does holding it. A session whose
    /// deadline slid forward on every page refresh would never expire, which is the deadline not
    /// existing.
    /// </summary>
    [Fact]
    public void HoldingASessionDoesNotExtendItsDeadline()
    {
        var session = Session();
        var deadline = session.ExpiresAtUtc;

        Assert.False(session.BeginHolding(Now.AddMinutes(5)).IsError);

        Assert.Equal(deadline, session.ExpiresAtUtc);
    }

    /// <summary>
    /// Re-pricing is refused once anything is held: changing what the customer is about to be
    /// charged after they have committed to a figure is the problem, not the solution.
    /// </summary>
    [Fact]
    public void AHeldSessionCannotBeRepriced()
    {
        var session = Session();
        Assert.False(session.BeginHolding(Now).IsError);

        var repriced = session.Reprice(
            Us,
            UsShipping,
            [new QuotedLine(Guid.NewGuid(), "SKU-1", "A thing", 1, 1.00m, Guid.NewGuid())],
            null,
            null,
            Now);

        Assert.True(repriced.IsError);
    }

    /// <summary>Terminal is terminal. A confirmed session cannot be expired out from under its order.</summary>
    [Fact]
    public void AConfirmedSessionCannotBeExpired()
    {
        var session = Session();
        PlacedOrder(session);

        Assert.True(session.Status.IsConfirmed);
        Assert.True(session.Expire(Now.AddHours(1)).IsError);
    }

    /// <summary>
    /// Expiry and cancellation are both terminal, and neither can follow the other. The expiry
    /// sweeper and a customer pressing cancel can race; the loser gets an error rather than
    /// writing a second terminal state and a second release event.
    /// </summary>
    [Fact]
    public void ATerminalSessionCannotBecomeTerminalTwice()
    {
        var expired = Session();
        Assert.False(expired.Expire(Now.AddHours(1)).IsError);
        Assert.True(expired.Expire(Now.AddHours(2)).IsError);
        Assert.True(expired.Cancel("too late", Now.AddHours(2)).IsError);

        var cancelled = Session();
        Assert.False(cancelled.Cancel("changed my mind", Now).IsError);
        Assert.True(cancelled.Expire(Now.AddHours(1)).IsError);
    }

    // ------------------------------------------------------------------ the order

    /// <summary>
    /// <b>An order can only come from a session that is holding.</b> Holding means TX 1
    /// succeeded: the stock, the promotion use and the points are reserved against the session's
    /// id.
    ///
    /// <para>
    /// An order for a session that never reserved its stock is the state Onyx shipped in —
    /// <b>D2</b>, where the order row was created first and the deductions came later, each
    /// committing separately.
    /// </para>
    /// </summary>
    [Fact]
    public void AnOrderCanOnlyBePlacedFromASessionThatIsHolding()
    {
        var open = Session();

        var tooEarly = Order.PlaceFrom(open, OrderNumber.FromSequence(1), Now);

        Assert.True(tooEarly.IsError);
        Assert.Equal(OrderErrors.SessionNotHolding.Code, tooEarly.FirstError.Code);
    }

    /// <summary>
    /// The order copies the quote verbatim rather than recomputing it.
    ///
    /// <para>
    /// Re-running the pipeline here would be a second place that prices a basket, and the two
    /// could disagree — Onyx <b>A6</b>'s shape moved one layer in. The session is the authority
    /// for what this costs; the order records what it cost.
    /// </para>
    /// </summary>
    [Fact]
    public void AnOrderCopiesTheQuoteRatherThanRecomputingIt()
    {
        var session = Session(
            unitPrice: 100.00m,
            promotion: new PromotionInput(Guid.NewGuid(), "SAVE20", 20.00m));

        var order = PlacedOrder(session);

        Assert.Equal(session.MerchandiseSubtotal.Amount, order.MerchandiseSubtotal.Amount);
        Assert.Equal(session.Shipping.Amount, order.Shipping.Amount);
        Assert.Equal(session.Tax.Amount, order.Tax.Amount);
        Assert.Equal(session.Total.Amount, order.Total.Amount);
        Assert.Equal(session.DiscountedMerchandise.Amount, order.QualifyingSpend.Amount);
        Assert.Equal(session.Id, order.CheckoutSessionId);
        Assert.True(order.TotalMatchesItsRows());
    }

    /// <summary>A new order is unpaid, and says so. <c>202 Accepted</c>, not <c>201 Created</c>.</summary>
    [Fact]
    public void ANewOrderIsPaymentPendingAndHasNoPaymentReference()
    {
        var order = PlacedOrder();

        Assert.True(order.Status.IsPaymentPending);
        Assert.Null(order.PaymentReference);
        Assert.Empty(order.Payments);
        Assert.Null(order.PaidAtUtc);
    }

    /// <summary>
    /// <b>M1 and M4.</b> Onyx set <c>Status = Paid</c> unconditionally after a loop that
    /// swallowed every exception, so a declined card produced a paid order; and when the gateway
    /// failed it substituted a locally generated six-digit number as the reference, so the
    /// reference looked valid and reconciliation was impossible.
    ///
    /// <para>
    /// Here a capture requires a non-empty provider reference, and the reference is recorded as a
    /// row that the unique index can police.
    /// </para>
    /// </summary>
    [Fact]
    public void MarkingAnOrderPaidRequiresAProviderReference()
    {
        var order = PlacedOrder();

        Assert.True(order.MarkPaid("", Now).IsError);
        Assert.True(order.MarkPaid("   ", Now).IsError);
        Assert.True(order.Status.IsPaymentPending);

        Assert.False(order.MarkPaid("pi_provider_123", Now).IsError);

        Assert.True(order.Status.IsPaid);
        Assert.Equal("pi_provider_123", order.PaymentReference);
        Assert.Equal(Now, order.PaidAtUtc);

        var payment = Assert.Single(order.Payments);
        Assert.Equal("pi_provider_123", payment.Reference);
        Assert.Equal(order.Total.Amount, payment.Amount);
    }

    /// <summary>A second capture is refused by the state machine, before the index is reached.</summary>
    [Fact]
    public void AnOrderCannotBePaidTwice()
    {
        var order = PlacedOrder();

        Assert.False(order.MarkPaid("pi_1", Now).IsError);

        var again = order.MarkPaid("pi_2", Now.AddMinutes(1));

        Assert.True(again.IsError);
        Assert.Equal("pi_1", order.PaymentReference);
        Assert.Single(order.Payments);
    }

    /// <summary>
    /// <b>Partial shipment</b>, which <c>ISSUES.md</c> §7 lists as a missing capability — Onyx
    /// had a single <c>Shipped</c> status that nothing ever assigned.
    ///
    /// <para>
    /// The order status is <i>derived</i> from the items rather than set beside them, so it
    /// cannot disagree with them: anything outstanding means <c>Fulfilling</c>, and nothing
    /// outstanding means <c>Shipped</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void AnOrderIsFulfillingWhileAnythingIsOutstandingAndShippedOnlyWhenNothingIs()
    {
        var session = CheckoutSession.Start(
            Guid.NewGuid(),
            Us,
            UsShipping,
            [new QuotedLine(Guid.NewGuid(), "SKU-1", "A thing", 3, 50.00m, Guid.NewGuid())],
            null,
            null,
            Now).Value;

        var order = PlacedOrder(session);
        Assert.False(order.MarkPaid("pi_1", Now).IsError);

        var item = order.Items.Single();

        Assert.False(order.Ship([(item.Id, 1)], Now).IsError);
        Assert.Equal(OrderStatusValue.Fulfilling, order.Status.Value);
        Assert.Equal(1, item.QuantityShipped);
        Assert.Equal(2, item.QuantityOutstanding);

        Assert.False(order.Ship([(item.Id, 2)], Now).IsError);
        Assert.Equal(OrderStatusValue.Shipped, order.Status.Value);
        Assert.True(item.IsFullyShipped);

        Assert.False(order.MarkDelivered(Now).IsError);
        Assert.Equal(OrderStatusValue.Delivered, order.Status.Value);
    }

    /// <summary>Shipping more than was ordered is refused rather than clamped.</summary>
    [Fact]
    public void ShippingMoreThanWasOrderedIsRefused()
    {
        var order = PlacedOrder();
        Assert.False(order.MarkPaid("pi_1", Now).IsError);

        var item = order.Items.Single();

        Assert.True(order.Ship([(item.Id, item.Quantity + 1)], Now).IsError);
        Assert.Equal(0, item.QuantityShipped);
    }

    /// <summary>An unpaid order cannot ship, and an unshipped one cannot be delivered.</summary>
    [Fact]
    public void TheLifecycleCannotBeSkipped()
    {
        var order = PlacedOrder();
        var item = order.Items.Single();

        Assert.True(order.Ship([(item.Id, 1)], Now).IsError);
        Assert.True(order.MarkDelivered(Now).IsError);
        Assert.True(order.MarkRefunded("re_1", Now).IsError);
    }

    /// <summary>
    /// A shipped order is too late to cancel: the goods have gone, so the correct instrument is
    /// a refund. Onyx's status endpoint would have accepted <c>"Cancelled"</c> on anything.
    /// </summary>
    [Fact]
    public void AShippedOrderCannotBeCancelled()
    {
        var order = PlacedOrder();
        Assert.False(order.MarkPaid("pi_1", Now).IsError);
        Assert.False(order.Ship([(order.Items.Single().Id, order.Items.Single().Quantity)], Now).IsError);

        Assert.True(order.Cancel("too late", Now).IsError);
        Assert.Equal(OrderStatusValue.Shipped, order.Status.Value);
    }

    /// <summary>
    /// Cancellation raises the event that releases the stock, the promotion use and the points —
    /// all keyed on the checkout session id, which is the one reference every hold was placed
    /// under.
    /// </summary>
    [Fact]
    public void CancellingAnOrderRaisesTheEventThatReleasesItsHolds()
    {
        var order = PlacedOrder();

        Assert.False(order.Cancel("out of stock at the warehouse", Now).IsError);

        var cancelled = Assert.Single(
            order.DomainEvents.OfType<Domain.OrderAggregate.Events.OrderCancelledEvent>());

        Assert.Equal(order.CheckoutSessionId, cancelled.CheckoutSessionId);
        Assert.Equal(OrderStatusValue.Cancelled, order.Status.Value);
    }

    /// <summary>A refund needs a provider reference, for the same reason a capture does.</summary>
    [Fact]
    public void ARefundRequiresAProviderReference()
    {
        var order = PlacedOrder();
        Assert.False(order.MarkPaid("pi_1", Now).IsError);

        Assert.True(order.MarkRefunded("", Now).IsError);
        Assert.False(order.MarkRefunded("re_provider_1", Now).IsError);
        Assert.Equal(OrderStatusValue.Refunded, order.Status.Value);
        Assert.True(order.MarkRefunded("re_provider_2", Now).IsError);
    }

    // ------------------------------------------------------------------ the number

    /// <summary>
    /// <b>D8's other half.</b> Onyx's order number came from a <c>static int</c> that reset to
    /// 1000 on every restart and was per-process, so numbers repeated across restarts and across
    /// instances — and nothing would have noticed, because the number was never written to the
    /// order at all, only logged.
    /// </summary>
    [Fact]
    public void TheSequenceAllocatesConsecutiveZeroPaddedNumbers()
    {
        var sequence = OrderNumberSequence.Create();

        Assert.Equal("BC-00000001", sequence.Allocate().Value);
        Assert.Equal("BC-00000002", sequence.Allocate().Value);
        Assert.Equal("BC-00000003", sequence.Allocate().Value);
    }

    /// <summary>
    /// Numbers round-trip through their string form, so a customer quoting one from an email can
    /// be looked up.
    /// </summary>
    [Theory]
    [InlineData("BC-00000001")]
    [InlineData("BC-00000123")]
    [InlineData("BC-99999999")]
    public void AnOrderNumberRoundTripsThroughItsText(string text)
    {
        var parsed = OrderNumber.Parse(text);

        Assert.False(parsed.IsError);
        Assert.Equal(text, parsed.Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000001")]
    [InlineData("BC-1")]
    [InlineData("XX-00000001")]
    [InlineData("BC-0000000A")]
    public void AMalformedOrderNumberIsRejected(string? text)
    {
        Assert.True(OrderNumber.Parse(text).IsError);
    }
}
