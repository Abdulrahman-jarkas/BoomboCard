using BoomboCard.SharedKernel;

namespace BoomboCard.Sales.Domain.Pricing;

/// <summary>
/// What kind of line this is. The order of the values is the order the pipeline produces them
/// in, which is also the order a receipt reads in.
/// </summary>
public enum QuoteAdjustmentKind
{
    /// <summary>A promotion code's discount. Negative.</summary>
    PromotionDiscount = 1,

    /// <summary>Loyalty points redeemed, as a priced discount. Negative.</summary>
    LoyaltyDiscount = 2,

    /// <summary>The delivery charge. Positive, possibly zero.</summary>
    Shipping = 3,

    /// <summary>Sales tax or VAT on the taxable base. Positive.</summary>
    Tax = 4
}

/// <summary>
/// One computed line of a quote: what it is, what it came to, and what it was calculated
/// against.
///
/// <para>
/// <b>Why every step of the pipeline persists a row instead of folding into one number.</b>
/// Onyx computed its total in a single expression with four interleaved rules and stored the
/// result as one <c>decimal Total</c> on the order. So afterwards nothing could say which rule
/// had produced which part of it — not support, not the customer, not the developer debugging
/// it. <b>M19</b> records the related failure: when order creation half-failed, the total was
/// never recomputed and the stored figure described a basket that no longer existed.
/// </para>
///
/// <para>
/// Here the total is <c>Σ(merchandise lines) + Σ(adjustments)</c> and nothing else, so it is
/// reconstructible from rows. <see cref="BasisAmount"/> is the figure the line was computed
/// from — a discount with no stated basis is unauditable, and it is the field that makes
/// "why is my tax $6.14?" answerable.
/// </para>
/// </summary>
public sealed class QuoteAdjustment : Entity
{
    public Guid CheckoutSessionId { get; private init; }

    public QuoteAdjustmentKind Kind { get; private init; }

    /// <summary>Shown to the customer: "WELCOME10 — 10% off", "Standard delivery", "Tax at 8%".</summary>
    public string Description { get; private init; } = string.Empty;

    /// <summary>
    /// Signed, in the basket's currency. Negative for a discount, positive for a charge.
    ///
    /// Stored as a plain decimal plus the session's currency rather than as an owned
    /// <c>Money</c>, because <c>Money</c> cannot represent a negative amount — and a discount is
    /// genuinely negative. The arithmetic is done in <c>Money</c> and the sign applied here, so
    /// no negative value ever exists inside a <c>Money</c>.
    /// </summary>
    public decimal Amount { get; private init; }

    /// <summary>What this line was calculated against. Zero where the notion does not apply.</summary>
    public decimal BasisAmount { get; private init; }

    /// <summary>
    /// The promotion this line came from, so the order can be tied back to the campaign that
    /// discounted it — and so campaign reporting can be reconciled against actual orders.
    /// </summary>
    public Guid? PromotionId { get; private init; }

    /// <summary>Points redeemed, for a <see cref="QuoteAdjustmentKind.LoyaltyDiscount"/> line.</summary>
    public int? PointsRedeemed { get; private init; }

    private QuoteAdjustment() { }

    internal static QuoteAdjustment Promotion(
        Guid sessionId,
        Guid promotionId,
        string description,
        Money discount,
        Money basis) =>
        new()
        {
            Id = Guid.NewGuid(),
            CheckoutSessionId = sessionId,
            Kind = QuoteAdjustmentKind.PromotionDiscount,
            Description = description,
            Amount = -discount.Amount,
            BasisAmount = basis.Amount,
            PromotionId = promotionId
        };

    internal static QuoteAdjustment Loyalty(
        Guid sessionId,
        int points,
        Money discount) =>
        new()
        {
            Id = Guid.NewGuid(),
            CheckoutSessionId = sessionId,
            Kind = QuoteAdjustmentKind.LoyaltyDiscount,
            Description = $"{points} loyalty points redeemed",
            Amount = -discount.Amount,
            BasisAmount = 0m,
            PointsRedeemed = points
        };

    internal static QuoteAdjustment Shipping(
        Guid sessionId,
        string description,
        Money charge,
        Money basis) =>
        new()
        {
            Id = Guid.NewGuid(),
            CheckoutSessionId = sessionId,
            Kind = QuoteAdjustmentKind.Shipping,
            Description = description,
            Amount = charge.Amount,
            BasisAmount = basis.Amount
        };

    internal static QuoteAdjustment Tax(
        Guid sessionId,
        string description,
        Money tax,
        Money taxableBase) =>
        new()
        {
            Id = Guid.NewGuid(),
            CheckoutSessionId = sessionId,
            Kind = QuoteAdjustmentKind.Tax,
            Description = description,
            Amount = tax.Amount,
            BasisAmount = taxableBase.Amount
        };

    public bool IsDiscount => Amount < 0m;
}
