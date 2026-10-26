using BoomboCard.Sales.Domain.CheckoutSessionAggregate.Events;
using BoomboCard.Sales.Domain.Pricing;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Sales.Domain.CheckoutSessionAggregate;

/// <summary>
/// A priced basket with a deadline: the quote the customer is shown, and the durable handle
/// every hold placed in another module names.
///
/// Invariants it holds:
///  - The total equals the sum of its lines and adjustments. Nothing else.
///  - A quote is priced before anything is held, and re-priced only while Open.
///  - Only a session that is Holding can be confirmed.
///  - Confirmed, Expired and Cancelled are terminal.
///  - The captured <c>PriceId</c> per line is what the customer was shown, and what is charged.
///
/// <para>
/// <b>Why this aggregate commits first.</b> The flow is three transactions across three modules:
/// </para>
///
/// <code>
/// TX 0 (Sales)     CheckoutSession, Open, ExpiresAt = now + 20m        &lt;- the anchor
/// TX 1 (others)    DeductStock / ReservePromotion / RedeemPoints       &lt;- all named by session id
/// TX 2 (Sales)     session.Confirm() + Order(PaymentPending)           &lt;- 202 Accepted
/// </code>
///
/// <para>
/// If the session were created in TX 2, a TX 2 failure would leave stock held against an id that
/// exists nowhere — invisible, permanent loss of sellable inventory, with nothing able to find it
/// because the only handle to it was never written. Committing the session first means every hold
/// references something the expiry sweeper can enumerate.
/// </para>
///
/// <para>
/// An invariant never crosses a boundary, so none of this is a distributed transaction. Stock
/// correctness is Catalog's alone; the three commits are sequenced and made recoverable by this
/// session plus the four recovery layers in <c>CONCEPTS.md</c> §13.
/// </para>
/// </summary>
public sealed class CheckoutSession : AggregateRoot
{
    /// <summary>
    /// Twenty minutes: long enough to find a card and type it, short enough that abandoned
    /// baskets do not strand sellable stock for an afternoon. It is the figure that bounds this
    /// system's exposure — without an expiry, a checkout that neither succeeds nor fails holds
    /// stock forever.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    public const int MaxLines = 100;

    private readonly List<CheckoutLine> _lines = [];
    private readonly List<QuoteAdjustment> _adjustments = [];

    public Guid CustomerId { get; private init; }

    /// <summary>Validated against <c>Country</c>, which supplies the currency and the tax rate.</summary>
    public string CountryCode { get; private init; } = string.Empty;

    public string CurrencyCode { get; private init; } = string.Empty;

    public CheckoutStatus Status { get; private set; } = CheckoutStatus.Open;

    public DateTimeOffset StartedAtUtc { get; private init; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public IReadOnlyCollection<CheckoutLine> Lines => _lines.AsReadOnly();

    public IReadOnlyCollection<QuoteAdjustment> Adjustments => _adjustments.AsReadOnly();

    // ---- the computed quote, stored so the figures the customer saw are the figures charged

    public Money MerchandiseSubtotal { get; private set; } = null!;

    public Money DiscountedMerchandise { get; private set; } = null!;

    public Money Shipping { get; private set; } = null!;

    public Money TaxableBase { get; private set; } = null!;

    public Money Tax { get; private set; } = null!;

    public Money Total { get; private set; } = null!;

    /// <summary>Set when the quote includes a promotion, so the reservation knows which.</summary>
    public Guid? PromotionId { get; private set; }

    /// <summary>Points the quote redeems, so the debit knows how many.</summary>
    public int PointsRedeemed { get; private set; }

    /// <summary>
    /// When TX 1 placed this session's holds, or <c>null</c> if it never got that far.
    ///
    /// <para>
    /// <b>The fact that makes a blind release safe.</b> A release handler sees a session that is
    /// already terminal, so <c>Status.IsHolding</c> is long gone by then — but what it needs to
    /// know is whether anything was <i>ever</i> held, which is a durable fact about the session's
    /// history rather than its current state.
    /// </para>
    ///
    /// <para>
    /// Without it, <c>OnCheckoutAbandonedReleaseHolds</c> read the session's <i>lines</i> and
    /// asked Catalog to restore them — and the lines describe what the basket contained, not what
    /// was taken off the shelf. A customer who abandoned an Open checkout got its contents added
    /// to stock. BUGS-AND-LESSONS section 13.
    /// </para>
    /// </summary>
    public DateTimeOffset? HoldsPlacedAtUtc { get; private set; }

    /// <summary>Whether TX 1 ever ran for this session.</summary>
    public bool EverHeldAnything => HoldsPlacedAtUtc is not null;

    private CheckoutSession() { }

    /// <summary>
    /// Creates a session and prices it in one step.
    ///
    /// <para>
    /// Priced at creation rather than lazily, because an unpriced session is a thing that can be
    /// confirmed before anybody has computed what it costs — which is <b>M19</b> exactly ("the
    /// order total is not recomputed after a partial failure"). A session always has a total, and
    /// that total always equals its rows.
    /// </para>
    /// </summary>
    public static ErrorOr<CheckoutSession> Start(
        Guid customerId,
        Country country,
        ShippingPolicy shipping,
        IReadOnlyList<QuotedLine> quotedLines,
        PromotionInput? promotion,
        LoyaltyInput? loyalty,
        DateTimeOffset nowUtc)
    {
        if (customerId == Guid.Empty)
        {
            return CheckoutErrors.CustomerRequired;
        }

        if (quotedLines.Count == 0)
        {
            return CheckoutErrors.NoLines;
        }

        if (quotedLines.Count > MaxLines)
        {
            return CheckoutErrors.TooManyLines(MaxLines);
        }

        var session = new CheckoutSession
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            CountryCode = country.Code,
            CurrencyCode = country.CurrencyCode,
            Status = CheckoutStatus.Open,
            StartedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + Lifetime
        };

        var built = session.BuildLines(quotedLines, country);

        if (built.IsError)
        {
            return built.Errors;
        }

        var priced = session.Price(country, shipping, promotion, loyalty);

        if (priced.IsError)
        {
            return priced.Errors;
        }

        session.RaiseDomainEvent(new CheckoutStartedEvent(
            session.Id,
            customerId,
            country.Code,
            session.Total.Amount,
            session.ExpiresAtUtc,
            nowUtc));

        return session;
    }

    /// <summary>
    /// Re-runs the pipeline against fresh inputs. Open sessions only.
    ///
    /// <para>
    /// Used when the customer changes the basket, applies a code, or when a
    /// <c>ProductPriceChangedIntegrationEvent</c> arrives for something in it. Refused once
    /// anything is held, because re-pricing a session whose stock is already reserved would
    /// change what the customer is about to be charged after they have committed to a figure.
    /// </para>
    /// </summary>
    public ErrorOr<Success> Reprice(
        Country country,
        ShippingPolicy shipping,
        IReadOnlyList<QuotedLine> quotedLines,
        PromotionInput? promotion,
        LoyaltyInput? loyalty,
        DateTimeOffset nowUtc)
    {
        if (!Status.IsOpen)
        {
            return Status.IsHolding
                ? CheckoutErrors.CannotRepriceWhileHolding
                : CheckoutStatusErrors.NotLive;
        }

        if (HasExpiredAt(nowUtc))
        {
            return CheckoutErrors.Expired;
        }

        if (quotedLines.Count is 0 or > MaxLines)
        {
            return quotedLines.Count == 0
                ? CheckoutErrors.NoLines
                : CheckoutErrors.TooManyLines(MaxLines);
        }

        var previousTotal = Total.Amount;

        _lines.Clear();
        _adjustments.Clear();

        var built = BuildLines(quotedLines, country);

        if (built.IsError)
        {
            return built.Errors;
        }

        var priced = Price(country, shipping, promotion, loyalty);

        if (priced.IsError)
        {
            return priced.Errors;
        }

        RaiseDomainEvent(new CheckoutRepricedEvent(Id, previousTotal, Total.Amount, nowUtc));

        return Result.Success;
    }

    // ---------------------------------------------------------------- the saga transitions

    /// <summary>
    /// Marks the session as holding its reservations — called in TX 2, after TX 1 succeeded.
    /// </summary>
    public ErrorOr<Success> BeginHolding(DateTimeOffset nowUtc)
    {
        if (HasExpiredAt(nowUtc))
        {
            return CheckoutErrors.Expired;
        }

        var status = Status.BeginHolding();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        // Recorded here, before the holds are actually placed, and deliberately so: a crash
        // midway through TX 1 must leave the session saying "holds may exist" rather than "no
        // holds exist". Catalog clamps the release to what it actually finds, so claiming too
        // much is harmless where claiming too little strands stock forever.
        HoldsPlacedAtUtc = nowUtc;

        RaiseDomainEvent(new CheckoutHoldingEvent(Id, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> Confirm(Guid orderId, DateTimeOffset nowUtc)
    {
        // Deliberately NOT checking expiry here.
        //
        // By this point stock has been deducted, the promotion use claimed and the points
        // debited, all against this session's id. Refusing to confirm because the clock passed
        // the deadline between TX 1 and TX 2 would leave those holds in place with no order to
        // justify them — and the customer has paid nothing yet but has had their basket taken.
        // The deadline exists to bound how long an ABANDONED checkout strands stock, not to
        // invalidate one that is completing. A session that reached Holding is finishing.
        var status = Status.Confirm();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        RaiseDomainEvent(new CheckoutConfirmedEvent(Id, orderId, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// Expires the session, recording whether anything was held so the release handler knows
    /// whether there is work to do — though it fires unconditionally regardless, because
    /// idempotent releases are what make the sweeper the layer the saga relies on.
    /// </summary>
    public ErrorOr<Success> Expire(DateTimeOffset nowUtc)
    {
        var wasHolding = Status.IsHolding;
        var status = Status.Expire();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        RaiseDomainEvent(new CheckoutExpiredEvent(Id, CustomerId, wasHolding, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> Cancel(string reason, DateTimeOffset nowUtc)
    {
        var wasHolding = Status.IsHolding;
        var status = Status.Cancel();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        RaiseDomainEvent(new CheckoutCancelledEvent(Id, CustomerId, wasHolding, reason, nowUtc));

        return Result.Success;
    }

    public bool HasExpiredAt(DateTimeOffset nowUtc) => ExpiresAtUtc <= nowUtc;

    /// <summary>
    /// The reference every other module's hold is keyed on. The session id as a string, so
    /// Catalog's <c>StockMovement.Reference</c>, Promotions' <c>CheckoutSessionId</c> and
    /// Loyalty's ledger reference are all the same value — which is what makes one release call
    /// per module able to find everything.
    /// </summary>
    public string HoldReference => Id.ToString();

    // ---------------------------------------------------------------- pricing

    private ErrorOr<Success> BuildLines(IReadOnlyList<QuotedLine> quotedLines, Country country)
    {
        // Duplicate products collapsed before anything is priced. The stock idempotency key is
        // per product and reference, so two lines for one product in a single checkout would
        // otherwise collide with themselves — Catalog's DeductStockCommandHandler groups for the
        // same reason, and doing it here too means the quote the customer sees matches.
        var collapsed = quotedLines
            .GroupBy(line => line.ProductId)
            .Select(group => group.First() with { Quantity = group.Sum(line => line.Quantity) })
            .ToList();

        foreach (var quoted in collapsed)
        {
            var unitPrice = Money.Create(quoted.UnitPrice, country.CurrencyCode);

            if (unitPrice.IsError)
            {
                return unitPrice.Errors;
            }

            var line = CheckoutLine.Create(
                Id,
                quoted.ProductId,
                quoted.Sku,
                quoted.Name,
                quoted.Quantity,
                unitPrice.Value,
                quoted.PriceId);

            if (line.IsError)
            {
                return line.Errors;
            }

            _lines.Add(line.Value);
        }

        return Result.Success;
    }

    private ErrorOr<Success> Price(
        Country country,
        ShippingPolicy shipping,
        PromotionInput? promotion,
        LoyaltyInput? loyalty)
    {
        var totals = QuoteCalculator.Calculate(
            _lines.Select(line => line.LineTotal).ToList(),
            country,
            shipping,
            promotion,
            loyalty);

        if (totals.IsError)
        {
            return totals.Errors;
        }

        var quote = totals.Value;

        MerchandiseSubtotal = quote.MerchandiseSubtotal;
        DiscountedMerchandise = quote.DiscountedMerchandise;
        Shipping = quote.Shipping;
        TaxableBase = quote.TaxableBase;
        Tax = quote.Tax;
        Total = quote.Total;

        PromotionId = promotion?.PromotionId;
        PointsRedeemed = loyalty?.Points ?? 0;

        // One adjustment row per step that produced a figure, in pipeline order, so the total is
        // reconstructible from rows rather than asserted. See QuoteAdjustment.
        if (promotion is not null && !quote.PromotionDiscount.IsZero)
        {
            _adjustments.Add(QuoteAdjustment.Promotion(
                Id,
                promotion.PromotionId,
                promotion.Description,
                quote.PromotionDiscount,
                quote.MerchandiseSubtotal));
        }

        if (loyalty is not null && !quote.LoyaltyDiscount.IsZero)
        {
            _adjustments.Add(QuoteAdjustment.Loyalty(Id, loyalty.Points, quote.LoyaltyDiscount));
        }

        _adjustments.Add(QuoteAdjustment.Shipping(
            Id,
            quote.Shipping.IsZero
                ? $"Free delivery (merchandise over {shipping.FreeAbove:0.00})"
                : "Standard delivery",
            quote.Shipping,
            // The basis recorded is the PRE-discount merchandise subtotal, because that is what
            // the threshold was tested against. Recording the discounted figure would make the
            // row describe a decision that was not taken (M8).
            quote.MerchandiseSubtotal));

        _adjustments.Add(QuoteAdjustment.Tax(
            Id,
            $"{country.Code} tax at {country.TaxRate * 100m:0.##}%"
                + (shipping.ShippingIsTaxable ? " (including delivery)" : " (excluding delivery)"),
            quote.Tax,
            quote.TaxableBase));

        return Result.Success;
    }

    /// <summary>
    /// The invariant <c>SalesIntegrityCheckJob</c> asserts: the stored total equals the sum of
    /// the rows that explain it.
    ///
    /// <para>
    /// Onyx stored one <c>decimal Total</c> computed by an expression and never checked it
    /// against anything — and <b>M19</b> is the consequence, a total that described a basket that
    /// no longer existed after a partial failure.
    /// </para>
    /// </summary>
    public bool TotalMatchesItsRows() =>
        _lines.Sum(line => line.LineTotal.Amount) + _adjustments.Sum(adjustment => adjustment.Amount)
            == Total.Amount;
}

/// <summary>
/// A line as the caller resolved it from Catalog: the product, how many, and the price that was
/// quoted with the id proving which price it was.
///
/// <para>
/// Note what the <i>client</i> sends, which is not this: product ids and quantities only. The
/// price and the <c>PriceId</c> come from Catalog's <c>GetProductPricingQuery</c> inside the
/// handler. <b>M2</b> — "the client never sends a money value" — holds at the wire, which is
/// where it matters.
/// </para>
/// </summary>
public sealed record QuotedLine(
    Guid ProductId,
    string Sku,
    string Name,
    int Quantity,
    decimal UnitPrice,
    Guid PriceId);
