using BoomboCard.Sales.Domain.Pricing;
using BoomboCard.SharedKernel;

namespace BoomboCard.Sales.Tests;

/// <summary>
/// The pricing pipeline. These are the tests that make <b>M6–M10</b> stay closed.
///
/// <para>
/// Everything here is a pure function of its arguments — no fixture, no database, no clock —
/// which is itself the point being demonstrated. Onyx's equivalent "domain service" took an
/// <c>IQueryable&lt;Order&gt;</c> and called <c>EF.Functions.Like</c> (<b>A11</b>), so no rule in
/// it could be exercised without a database, and consequently none of them were.
/// </para>
/// </summary>
public class QuoteCalculatorTests
{
    private static readonly Country Us = Country.UnitedStates;
    private static readonly ShippingPolicy UsShipping = ShippingPolicy.UnitedStates;

    private static Money Usd(decimal amount) => Money.Create(amount, "USD").Value;

    private static QuoteTotals Quote(
        decimal merchandise,
        decimal promotionDiscount = 0m,
        decimal loyaltyDiscount = 0m)
    {
        var result = QuoteCalculator.Calculate(
            [Usd(merchandise)],
            Us,
            UsShipping,
            promotionDiscount > 0m
                ? new PromotionInput(Guid.NewGuid(), "promo", promotionDiscount)
                : null,
            loyaltyDiscount > 0m
                ? new LoyaltyInput((int)(loyaltyDiscount * 100), loyaltyDiscount)
                : null);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);

        return result.Value;
    }

    // ------------------------------------------------------------------ M8

    /// <summary>
    /// <b>M8, and the test the obligation line asks for by name:</b> "a monotonicity property
    /// test over the total".
    ///
    /// <para>
    /// <b>Stating the property correctly is most of the work, and the first attempt at this test
    /// got it wrong.</b> "The total never falls as the basket grows" is the obvious phrasing and
    /// it is false by design: at the free-shipping threshold the total drops by the delivery
    /// charge, because that is what a free-delivery offer <i>is</i>. $74.99 costs $87.46 and
    /// $75.00 costs $81.00, and both figures are correct.
    /// </para>
    ///
    /// <para>
    /// So the property that actually distinguishes the fix from the bug is piecewise: <b>within
    /// a shipping band the total is strictly increasing in the basket</b>. Onyx's totals were
    /// not, because its two rules used the shared $75 figure with different operators against
    /// different bases — a volume discount at <c>&gt; 75</c> on the gross and free shipping at
    /// <c>&gt;= 75</c> on the discounted figure. The discount switching on at $76 pushed the
    /// basket back under the shipping threshold, so the total went <i>up</i> and then came back
    /// down:
    /// </para>
    ///
    /// <code>
    /// $75.00 -> $81.00
    /// $76.00 -> $83.97     &lt;- up
    /// $80.00 -> $82.08     &lt;- and back down
    /// </code>
    ///
    /// <para>
    /// A bump, not a step. Every subtotal in <c>(75.00, 78.95)</c> fell into it.
    /// </para>
    ///
    /// <para>
    /// Exhaustive over 0–500 in 1-cent steps — 50,000 baskets, every rounding boundary in the
    /// range, in well under a second because the pipeline is pure. A randomised property test
    /// would sample this space; stepping it leaves nothing to luck.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.00)]
    [InlineData(5.00)]
    [InlineData(20.00)]
    public void WithinAShippingBandTheTotalNeverFallsAsTheBasketGrows(decimal promotionDiscount)
    {
        var previous = decimal.MinValue;
        var previousMerchandise = 0m;
        var previouslyShippedFree = false;

        for (var cents = 1; cents <= 50_000; cents++)
        {
            var merchandise = cents / 100m;
            var quote = Quote(merchandise, promotionDiscount);
            var shipsFree = quote.Shipping.Amount == 0m;

            // Crossing into free shipping is the one permitted discontinuity.
            if (shipsFree != previouslyShippedFree)
            {
                previous = quote.Total.Amount;
                previousMerchandise = merchandise;
                previouslyShippedFree = shipsFree;
                continue;
            }

            Assert.True(
                quote.Total.Amount >= previous,
                $"Adding to the basket made the order cheaper without crossing the shipping "
                + $"threshold: {previousMerchandise:0.00} cost {previous:0.00}, but "
                + $"{merchandise:0.00} costs {quote.Total.Amount:0.00}. That is Onyx M8 "
                + "reopened — the shipping threshold is being evaluated against a figure a "
                + "discount can lower.");

            previous = quote.Total.Amount;
            previousMerchandise = merchandise;
        }
    }

    /// <summary>
    /// The other half of the property, and the one that catches Onyx's bump directly: <b>the
    /// total drops in exactly one place, and that place is the stated threshold.</b>
    ///
    /// <para>
    /// Onyx's total dropped at <c>75 + discount</c> — a point that moved with whatever promotion
    /// happened to apply — and rose at $76 on the way there. This asserts the drop happens at
    /// $75.00 and nowhere else, and that its size is bounded by the delivery charge plus the tax
    /// on it, so a drop cannot be hiding a second defect.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.00)]
    [InlineData(5.00)]
    [InlineData(20.00)]
    public void TheTotalDropsOnlyAtTheFreeShippingThreshold(decimal promotionDiscount)
    {
        var drops = new List<(decimal Merchandise, decimal Amount)>();
        var previous = decimal.MinValue;

        for (var cents = 1; cents <= 50_000; cents++)
        {
            var merchandise = cents / 100m;
            var total = Quote(merchandise, promotionDiscount).Total.Amount;

            if (previous != decimal.MinValue && total < previous)
            {
                drops.Add((merchandise, previous - total));
            }

            previous = total;
        }

        var single = Assert.Single(drops);

        Assert.Equal(UsShipping.FreeAbove, single.Merchandise);

        // Bounded by the delivery charge and the tax on it, which is the whole of the saving.
        Assert.True(
            single.Amount <= UsShipping.FlatRate * (1 + Us.TaxRate),
            $"The total dropped {single.Amount:0.00} at the threshold, which is more than the "
            + $"{UsShipping.FlatRate * (1 + Us.TaxRate):0.00} delivery charge being waived.");
    }

    /// <summary>
    /// <b>The single assertion that is M8.</b> A basket ships free if and only if its
    /// <i>pre-discount</i> merchandise reaches the threshold — for every discount, including one
    /// large enough to take the basket to nothing.
    ///
    /// <para>
    /// This is what Onyx got wrong, stated without reference to monotonicity at all: its
    /// threshold moved with the discount, so the answer to "does this ship free?" depended on a
    /// promotion the shipping rule had no business knowing about. Change
    /// <c>ShippingPolicy.QualifiesForFreeShipping</c>'s argument to the discounted figure and
    /// every row below fails.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.00)]
    [InlineData(0.01)]
    [InlineData(5.00)]
    [InlineData(74.99)]
    [InlineData(500.00)]
    public void ADiscountNeverMovesTheFreeShippingThreshold(decimal promotionDiscount)
    {
        foreach (var cents in new[] { 7_000, 7_498, 7_499, 7_500, 7_501, 8_000, 20_000 })
        {
            var merchandise = cents / 100m;
            var quote = Quote(merchandise, promotionDiscount);

            var shipsFree = quote.Shipping.Amount == 0m;
            var reachesThreshold = merchandise >= UsShipping.FreeAbove;

            Assert.Equal(reachesThreshold, shipsFree);
        }
    }

    /// <summary>
    /// The exact band Onyx's own register tabulated, asserted directly rather than only as a
    /// consequence of the property above — so a failure says "M8" rather than "something in
    /// 0–500".
    ///
    /// <para>
    /// A $5 discount takes a $76 basket to $71 of merchandise, which is under the $75 threshold.
    /// Onyx would have charged $5.99 delivery on it while shipping the $75 basket free. Here
    /// both ship free, because both put $75+ of goods in the basket.
    /// </para>
    /// </summary>
    [Fact]
    public void ADiscountCannotTakeABasketBackUnderTheFreeShippingThreshold()
    {
        var atThreshold = Quote(75.00m, promotionDiscount: 5.00m);
        var justOver = Quote(76.00m, promotionDiscount: 5.00m);

        Assert.Equal(0m, atThreshold.Shipping.Amount);
        Assert.Equal(0m, justOver.Shipping.Amount);

        // 71.00 of goods, 0 shipping, 8% tax on 71.00 = 5.68.
        Assert.Equal(76.68m, justOver.Total.Amount);
        Assert.True(justOver.Total.Amount > atThreshold.Total.Amount);
    }

    // ------------------------------------------------------------------ M9

    /// <summary>
    /// <b>M9 — the boundary itself.</b> Onyx compared the same $75 figure with <c>&gt; 75</c> in
    /// one rule and <c>&gt;= 75</c> in the other, so exactly $75.00 got free shipping and no
    /// volume discount, and the two rules disagreed about the number they shared.
    ///
    /// <para>
    /// One constant, one comparison, and the test pins which side of it the boundary falls on:
    /// <b>exactly the threshold qualifies</b>. One cent under does not.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(74.98, 5.99)]
    [InlineData(74.99, 5.99)]
    [InlineData(75.00, 0.00)]   // the boundary. Exactly enough IS enough.
    [InlineData(75.01, 0.00)]
    public void FreeShippingAppliesAtExactlyTheThresholdAndNotBelowIt(
        decimal merchandise,
        decimal expectedShipping)
    {
        Assert.Equal(expectedShipping, Quote(merchandise).Shipping.Amount);
    }

    /// <summary>
    /// The same boundary read off the policy directly, so a failure distinguishes "the rule is
    /// wrong" from "the pipeline calls it wrongly".
    ///
    /// <para>
    /// It is the same <c>&gt;=</c> decision Catalog's <c>StockLevel.CanFulfil</c> makes about
    /// availability (Onyx <b>M18</b> used <c>&lt;=</c> there and reported a product with exactly
    /// enough stock as short). "Exactly enough is enough" is one rule applied consistently, not
    /// a coincidence.
    /// </para>
    /// </summary>
    [Fact]
    public void TheShippingThresholdIsInclusive()
    {
        Assert.False(UsShipping.QualifiesForFreeShipping(Usd(74.99m)));
        Assert.True(UsShipping.QualifiesForFreeShipping(Usd(75.00m)));
        Assert.True(UsShipping.QualifiesForFreeShipping(Usd(75.01m)));
    }

    /// <summary>
    /// Each country's threshold is its own. AED 100 is well over the US threshold of 75 and well
    /// under the UAE's 200, so a policy that ignored the country would ship it free.
    /// </summary>
    [Fact]
    public void EachCountryUsesItsOwnThreshold()
    {
        var aed = QuoteCalculator.Calculate(
            [Money.Create(100.00m, "AED").Value],
            Country.UnitedArabEmirates,
            ShippingPolicy.UnitedArabEmirates,
            promotion: null,
            loyalty: null);

        Assert.False(aed.IsError);
        Assert.Equal(25.00m, aed.Value.Shipping.Amount);
        Assert.Equal("AED", aed.Value.Total.CurrencyCode);
    }

    // ------------------------------------------------------------------ M10

    /// <summary>
    /// <b>M10 — the tax base is stated and tested.</b> The register's complaint was not that
    /// Onyx excluded shipping from tax; it was that the choice was "undocumented and untested,
    /// so it cannot be distinguished from a bug".
    ///
    /// <para>
    /// Both configured countries tax delivery, so the assertion is that tax is charged on
    /// merchandise <i>plus</i> shipping. $50 of goods, $5.99 delivery, 8% on $55.99 = $4.48.
    /// Excluding shipping would give $4.00 — a 48-cent difference that no test would have
    /// caught before.
    /// </para>
    /// </summary>
    [Fact]
    public void TaxIsChargedOnMerchandisePlusShippingWhereThePolicySaysShippingIsTaxable()
    {
        var quote = Quote(50.00m);

        Assert.True(UsShipping.ShippingIsTaxable);
        Assert.Equal(5.99m, quote.Shipping.Amount);
        Assert.Equal(55.99m, quote.TaxableBase.Amount);
        Assert.Equal(4.48m, quote.Tax.Amount);
        Assert.Equal(60.47m, quote.Total.Amount);
    }

    /// <summary>
    /// Tax follows the discount down. Taxing the pre-discount figure would charge the customer
    /// tax on money they did not pay, which is the kind of error that is small per order and
    /// systematic across all of them.
    /// </summary>
    [Fact]
    public void TaxIsChargedOnTheDiscountedMerchandiseNotTheGross()
    {
        var quote = Quote(100.00m, promotionDiscount: 20.00m);

        // 100 merchandise, free shipping (100 >= 75 pre-discount), 20 off, 8% on 80.00.
        Assert.Equal(80.00m, quote.DiscountedMerchandise.Amount);
        Assert.Equal(0m, quote.Shipping.Amount);
        Assert.Equal(80.00m, quote.TaxableBase.Amount);
        Assert.Equal(6.40m, quote.Tax.Amount);
        Assert.Equal(86.40m, quote.Total.Amount);
    }

    // ------------------------------------------------------------------ M6, M7

    /// <summary>
    /// <b>M6 and M7.</b> Onyx cast <c>decimal</c> to <c>double</c>, accumulated in floating
    /// point, cast back, and never rounded — a real order total in its register was
    /// <c>83.096766</c>.
    ///
    /// <para>
    /// Every figure out of the pipeline has at most two decimal places, because <c>Money</c>
    /// rounds on every operation. The check is over a range rather than one case: a rounding
    /// error needs an input that produces one, and $1 at 8% does not.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryFigureIsRoundedToTwoDecimalPlaces()
    {
        for (var cents = 1; cents <= 20_000; cents++)
        {
            var quote = Quote(cents / 100m);

            foreach (var (name, amount) in new (string, decimal)[]
                     {
                         ("merchandise", quote.MerchandiseSubtotal.Amount),
                         ("discounted", quote.DiscountedMerchandise.Amount),
                         ("shipping", quote.Shipping.Amount),
                         ("taxable base", quote.TaxableBase.Amount),
                         ("tax", quote.Tax.Amount),
                         ("total", quote.Total.Amount)
                     })
            {
                Assert.Equal(
                    amount,
                    decimal.Round(amount, 2, MidpointRounding.ToEven));
            }
        }
    }

    /// <summary>
    /// The invariant <c>SalesIntegrityCheckJob</c> asserts in production, asserted here over a
    /// range: the total is the sum of the parts, exactly, with nothing lost to rounding.
    ///
    /// <para>
    /// Onyx stored a total computed by one expression and never compared it to anything —
    /// <b>M19</b> — which is why a total that described a basket that no longer existed could
    /// survive a partial failure unnoticed.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(10.00, 0.00, 0.00)]
    [InlineData(74.99, 0.00, 0.00)]
    [InlineData(75.00, 0.00, 0.00)]
    [InlineData(100.00, 20.00, 0.00)]
    [InlineData(100.00, 0.00, 15.00)]
    [InlineData(100.00, 20.00, 15.00)]
    [InlineData(33.33, 11.11, 0.00)]
    public void TheTotalIsExactlyTheSumOfItsParts(
        decimal merchandise,
        decimal promotionDiscount,
        decimal loyaltyDiscount)
    {
        var quote = Quote(merchandise, promotionDiscount, loyaltyDiscount);

        Assert.Equal(
            quote.DiscountedMerchandise.Amount + quote.Shipping.Amount + quote.Tax.Amount,
            quote.Total.Amount);

        Assert.Equal(
            quote.MerchandiseSubtotal.Amount
                - quote.PromotionDiscount.Amount
                - quote.LoyaltyDiscount.Amount,
            quote.DiscountedMerchandise.Amount);
    }

    // ------------------------------------------------------------------ the floor

    /// <summary>
    /// Two generous discounts on a small basket floor the merchandise at zero rather than going
    /// negative.
    ///
    /// <para>
    /// A negative merchandise figure would flow into the tax base and produce a <i>negative
    /// tax</i> — a refund nobody authorised, funded by the tax authority. <c>Money</c> cannot
    /// represent a negative amount, so the alternative to the floor is an exception on a path a
    /// customer can trigger by redeeming enough points on a cheap basket.
    /// </para>
    /// </summary>
    [Fact]
    public void TwoDiscountsLargerThanTheBasketFloorTheMerchandiseAtZero()
    {
        var quote = Quote(20.00m, promotionDiscount: 15.00m, loyaltyDiscount: 10.00m);

        Assert.Equal(0m, quote.DiscountedMerchandise.Amount);

        // Shipping is still charged — it is decided on the PRE-discount merchandise of 20.00,
        // which is under the threshold. The customer pays delivery and tax on delivery, and
        // nothing for the goods.
        Assert.Equal(5.99m, quote.Shipping.Amount);
        Assert.Equal(5.99m, quote.TaxableBase.Amount);
        Assert.Equal(0.48m, quote.Tax.Amount);
        Assert.Equal(6.47m, quote.Total.Amount);
    }

    /// <summary>
    /// Discounts that exactly consume the basket are the boundary of the floor, and must not be
    /// treated as the negative case.
    /// </summary>
    [Fact]
    public void ADiscountEqualToTheBasketLeavesZeroMerchandise()
    {
        var quote = Quote(20.00m, promotionDiscount: 20.00m);

        Assert.Equal(0m, quote.DiscountedMerchandise.Amount);
        Assert.Equal(20.00m, quote.PromotionDiscount.Amount);
    }

    // ------------------------------------------------------------------ qualifying spend

    /// <summary>
    /// What Loyalty earns on: merchandise after discounts, excluding tax and shipping.
    ///
    /// <para>
    /// Computed in one place so Sales and Loyalty cannot derive it differently. Earning on the
    /// gross total would pay loyalty on tax — money collected on behalf of a government — and on
    /// delivery, which rewards buying from further away.
    /// </para>
    /// </summary>
    [Fact]
    public void QualifyingSpendIsMerchandiseAfterDiscountsOnly()
    {
        var quote = Quote(50.00m, promotionDiscount: 10.00m);

        Assert.Equal(40.00m, QuoteCalculator.QualifyingSpend(quote).Amount);

        // Explicitly NOT the total, which includes 5.99 shipping and tax on both.
        Assert.NotEqual(quote.Total.Amount, QuoteCalculator.QualifyingSpend(quote).Amount);
    }

    // ------------------------------------------------------------------ guards

    /// <summary>An empty basket is a validation error, not a free order.</summary>
    [Fact]
    public void AQuoteWithNoLinesIsRejected()
    {
        var result = QuoteCalculator.Calculate([], Us, UsShipping, promotion: null, loyalty: null);

        Assert.True(result.IsError);
        Assert.Equal("Quote.NoLines", result.FirstError.Code);
    }

    /// <summary>
    /// Every country in <c>Country.All</c> has a shipping policy. A country added to one list
    /// and not the other would fail every checkout for that country at runtime, which is the
    /// kind of gap a registry test catches and a reviewer does not.
    /// </summary>
    [Fact]
    public void EveryCountryHasAShippingPolicy()
    {
        foreach (var country in Country.All)
        {
            var policy = ShippingPolicy.ForCountry(country);

            Assert.False(
                policy.IsError,
                $"{country.Code} has no shipping policy, so every checkout for it would fail.");
        }
    }
}
