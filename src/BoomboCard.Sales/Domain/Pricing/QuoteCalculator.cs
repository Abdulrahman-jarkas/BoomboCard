using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Sales.Domain.Pricing;

/// <summary>What a promotion contributes, as the caller already resolved it.</summary>
public sealed record PromotionInput(Guid PromotionId, string Description, decimal DiscountAmount);

/// <summary>What a loyalty redemption contributes.</summary>
public sealed record LoyaltyInput(int Points, decimal DiscountAmount);

/// <summary>
/// The seven steps, their results, and the figure each one was computed from.
///
/// <para>
/// <b>Every field is a distinct <c>Money</c> instance</b>, even where two of them hold the same
/// value. Both aggregates map these to separate EF owned types, and an owned instance belongs to
/// exactly one navigation — so aliasing two of them makes the aggregate unsaveable, with an
/// error that names shadow properties rather than the aliasing. BUGS-AND-LESSONS section 12.
/// </para>
///
/// <para>
/// The three that need saying out loud. <c>MerchandiseSubtotal</c> is step 2 — the sum of the
/// line extendeds, <b>before any discount</b>, and the basis the free shipping threshold is
/// tested against (see <c>ShippingPolicy.QualifiesForFreeShipping</c>).
/// <c>DiscountedMerchandise</c> is step 4's result: merchandise less both discounts, floored at
/// zero. <c>TaxableBase</c> is step 6's basis: discounted merchandise, plus shipping when the
/// policy says shipping is taxable.
/// </para>
/// </summary>
public sealed record QuoteTotals(
    Money MerchandiseSubtotal,
    Money PromotionDiscount,
    Money LoyaltyDiscount,
    Money DiscountedMerchandise,
    Money Shipping,
    Money TaxableBase,
    Money Tax,
    Money Total);

/// <summary>
/// The pricing pipeline. One ordered, named sequence of steps, in one place.
///
/// <para>
/// <b>This class replaces twenty lines that carried six findings.</b> Onyx's
/// <c>OrderPricingService.CalculateTotal</c>:
/// </para>
///
/// <code>
/// double subtotal = 0;
/// foreach (var line in order.Lines) subtotal += (double)line.UnitPrice * line.Quantity;
/// if (subtotal &gt; 75) subtotal *= 0.95;              // volume discount
/// subtotal -= subtotal * discountRate;                 // promo
/// var shipping = Money.Zero();
/// shipping.Amount = subtotal &gt;= 75 ? 0m : 5.99m;    // on the DISCOUNTED subtotal
/// var taxed = subtotal * 1.08;                         // shipping excluded
/// return (decimal)taxed + shipping.Amount;
/// </code>
///
/// <list type="bullet">
/// <item><b>M6</b> — <c>decimal</c> cast to <c>double</c>, accumulated in floating point, cast back.</item>
/// <item><b>M7</b> — never rounded. A real order total was <c>83.096766</c>.</item>
/// <item><b>M8</b> — shipping decided on the post-discount subtotal, so totals were non-monotonic.</item>
/// <item><b>M9</b> — the same $75 as <c>&gt; 75</c> in one rule and <c>&gt;= 75</c> in the other.</item>
/// <item><b>M10</b> — shipping excluded from tax, undocumented, so indistinguishable from a bug.</item>
/// <item><b>A43</b> — <c>Money.Zero()</c> followed by <c>shipping.Amount = …</c>, discarding the currency.</item>
/// </list>
///
/// <para>
/// <b>Pure, and deliberately so.</b> It takes resolved inputs and returns totals: no repository,
/// no <c>IQueryable</c>, no clock, no I/O. Onyx's "domain service" took an
/// <c>IQueryable&lt;Order&gt;</c> and called <c>EF.Functions.Like</c> (<b>A11</b>). This is a
/// function of its arguments, which is why every rule in it can be property-tested.
/// </para>
/// </summary>
public static class QuoteCalculator
{
    /// <summary>
    /// Runs the pipeline.
    ///
    /// <para>
    /// The order of the steps is the whole design, so it is spelled out rather than implied:
    /// </para>
    ///
    /// <list type="number">
    /// <item>line extended = unit × quantity, <b>rounded per line</b></item>
    /// <item><c>MerchandiseSubtotal</c> = Σ lines</item>
    /// <item>promotion discount, against the merchandise subtotal</item>
    /// <item>loyalty discount, as a priced line — and the running merchandise figure floored at zero</item>
    /// <item>shipping, decided on the <b>pre-discount</b> merchandise subtotal (<b>M8</b>)</item>
    /// <item>tax on discounted merchandise <b>plus shipping where the policy says shipping is taxable</b> (<b>M10</b>)</item>
    /// <item>total = discounted merchandise + shipping + tax</item>
    /// </list>
    /// </summary>
    public static ErrorOr<QuoteTotals> Calculate(
        IReadOnlyCollection<Money> lineExtendeds,
        Country country,
        ShippingPolicy shipping,
        PromotionInput? promotion,
        LoyaltyInput? loyalty)
    {
        if (lineExtendeds.Count == 0)
        {
            return Error.Validation("Quote.NoLines", "A quote must have at least one line.");
        }

        var currency = country.CurrencyCode;

        // ---- step 2: the merchandise subtotal.
        //
        // Money.Add rounds on every operation, so there is no accumulating fractional error to
        // carry forward. Step 1 — rounding each line's unit × quantity — happens in
        // CheckoutLine, at the point the line is captured, because that is the figure the
        // customer is shown per line and it has to be the figure that is summed.
        var merchandiseSubtotal = lineExtendeds.Aggregate(
            Money.Zero(currency),
            (running, line) => running.Add(line));

        // ---- step 3: the promotion discount.
        var promotionDiscount = Money.Zero(currency);

        if (promotion is { DiscountAmount: > 0m })
        {
            var discount = Money.Create(promotion.DiscountAmount, currency);

            if (discount.IsError)
            {
                return discount.Errors;
            }

            promotionDiscount = discount.Value;
        }

        // ---- step 4: the loyalty discount.
        var loyaltyDiscount = Money.Zero(currency);

        if (loyalty is { DiscountAmount: > 0m })
        {
            var discount = Money.Create(loyalty.DiscountAmount, currency);

            if (discount.IsError)
            {
                return discount.Errors;
            }

            loyaltyDiscount = discount.Value;
        }

        // Floored at zero. Two generous discounts on a small basket must not produce a negative
        // merchandise figure that then flows into the tax base — Money cannot represent one, and
        // the alternative is a negative tax, which is a refund nobody authorised.
        //
        // Each discount is individually capped by the module that produced it (Promotions caps
        // at its basis, Loyalty at the balance), so this floor only bites when the two combine.
        var totalDiscount = promotionDiscount.Add(loyaltyDiscount);

        var discountedMerchandise = totalDiscount.Amount >= merchandiseSubtotal.Amount
            ? Money.Zero(currency)
            : merchandiseSubtotal.Subtract(totalDiscount);

        // ---- step 5: shipping, on the PRE-DISCOUNT merchandise subtotal.
        //
        // This is M8. The basis is named, it is the one figure that rises monotonically with
        // what the customer puts in the basket, and no later step can pull it back under the
        // threshold. QuoteCalculatorTests asserts the resulting total is monotonic across
        // 0–500 in 1-cent steps — which is exactly what M8's obligation line asks for.
        var shippingCharge = shipping.ChargeFor(merchandiseSubtotal);

        // ---- step 6: tax.
        //
        // This is M10. Whether shipping is in the base is a stated per-country policy with the
        // jurisdiction rule recorded on ShippingPolicy, not an assumption either way — the
        // register's complaint was that Onyx's choice was "undocumented and untested, so it
        // cannot be distinguished from a bug".
        // Copy rather than alias on the not-taxable branch. Both configured countries tax
        // delivery today, so this branch is unreached — but QuoteTotals' fields land in separate
        // EF owned types, and an owned instance belongs to exactly one navigation, so a future
        // non-taxable policy would make the session unsaveable. BUGS-AND-LESSONS section 12.
        var taxableBase = shipping.ShippingIsTaxable
            ? discountedMerchandise.Add(shippingCharge)
            : Money.Create(discountedMerchandise.Amount, currency).Value;

        var tax = country.TaxOn(taxableBase);

        // ---- step 7: the total.
        var total = discountedMerchandise.Add(shippingCharge).Add(tax);

        return new QuoteTotals(
            merchandiseSubtotal,
            promotionDiscount,
            loyaltyDiscount,
            discountedMerchandise,
            shippingCharge,
            taxableBase,
            tax,
            total);
    }

    /// <summary>
    /// How much of the total counts as qualifying spend for loyalty earning.
    ///
    /// <para>
    /// <b>Merchandise after discounts, and nothing else</b> — the definition
    /// <c>EarnRule.PointsFor</c> documents and this is the one place that computes it. Tax is
    /// money collected for a government, so paying loyalty on it funds a reward out of somebody
    /// else's money; shipping is a pass-through cost, so earning on it rewards buying from
    /// further away; and discounts come off first, because earning on the pre-discount figure
    /// makes a promotion cost margin twice.
    /// </para>
    /// </summary>
    public static Money QualifyingSpend(QuoteTotals totals) =>
        // A copy, for the reason the taxable-base branch above copies: the caller stores this
        // beside DiscountedMerchandise, and two owned navigations cannot share one instance.
        Money.Create(
            totals.DiscountedMerchandise.Amount,
            totals.DiscountedMerchandise.CurrencyCode).Value;
}
