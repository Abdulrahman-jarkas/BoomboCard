using BoomboCard.Promotions.Contracts;
using BoomboCard.Promotions.Domain.PromotionAggregate;
using BoomboCard.Promotions.Domain.PromotionAggregate.Events;
using BoomboCard.SharedKernel;

namespace BoomboCard.Promotions.Tests;

/// <summary>
/// The <see cref="Promotion"/> aggregate's rules. Pure unit tests — no database, no container.
/// </summary>
public sealed class PromotionTests
{
    private static readonly DateTimeOffset Jan1 = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Feb1 = new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan15 = new(2027, 1, 15, 0, 0, 0, TimeSpan.Zero);

    private static Money Usd(decimal amount) => Money.Create(amount, "USD").Value;

    private static Promotion Create(
        Discount? discount = null,
        Money? minimumSpend = null,
        int? maxRedemptions = null,
        int? maxPerCustomer = null,
        PromotionScope? scope = null,
        StackingPolicy stacking = StackingPolicy.Exclusive,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null) =>
        Promotion.Create(
            PromotionCode.Create("WELCOME10").Value,
            "10% off your first order",
            discount ?? Discount.Percentage(0.10m).Value,
            ValidityWindow.Create(startsAt ?? Jan1, endsAt ?? Feb1).Value,
            scope ?? PromotionScope.EntireBasket,
            stacking,
            minimumSpend,
            maxRedemptions,
            maxPerCustomer,
            Jan1).Value;

    private static Promotion Active(
        Discount? discount = null,
        Money? minimumSpend = null,
        int? maxRedemptions = null,
        int? maxPerCustomer = null,
        PromotionScope? scope = null)
    {
        var promotion = Create(discount, minimumSpend, maxRedemptions, maxPerCustomer, scope);
        Assert.False(promotion.Activate(Jan1).IsError);
        return promotion;
    }

    // ----------------------------------------------------------------- lifecycle (Onyx M16)

    [Fact]
    public void ANewPromotionIsADraft_AndADraftIsNotUsable()
    {
        var promotion = Create();

        Assert.True(promotion.Status.IsDraft);

        // Onyx had no state in which a campaign could be configured and reviewed before
        // customers could use it, because it had no state at all — its promotions were two
        // entries in a static dictionary.
        var evaluated = promotion.Evaluate(Usd(100), Usd(100), 0, Jan15);

        Assert.True(evaluated.IsError);
        Assert.Equal("Promotion.CodeNotRecognised", evaluated.FirstError.Code);
    }

    [Fact]
    public void APromotionWhoseWindowHasAlreadyEnded_CannotBeActivated()
    {
        var promotion = Create(startsAt: Jan1, endsAt: Feb1);

        // This is M16 directly: Onyx shipped SUMMER25 live with an expiry of 2026-09-01, before
        // the audit that found it, and nothing anywhere could have noticed.
        var activated = promotion.Activate(Feb1.AddDays(1));

        Assert.True(activated.IsError);
        Assert.Equal("Promotion.WindowAlreadyEnded", activated.FirstError.Code);
    }

    [Fact]
    public void APausedPromotionReportsAsUnrecognised_NotAsPaused()
    {
        var promotion = Active();
        Assert.False(promotion.Pause(Jan15).IsError);

        // Internal campaign state is not something a shopper can act on, so it is not leaked
        // back to them. The distinction is still in the status column for the merchandiser.
        var evaluated = promotion.Evaluate(Usd(100), Usd(100), 0, Jan15);

        Assert.Equal("Promotion.CodeNotRecognised", evaluated.FirstError.Code);
        Assert.True(promotion.Status.IsPaused);
    }

    [Fact]
    public void AClosedPromotionCannotBeReopened()
    {
        var promotion = Active();
        Assert.False(promotion.Close(PromotionCloseReason.Withdrawn, Jan15).IsError);

        // Terminal by design: reopening an exhausted campaign would let a "first 100 customers"
        // promotion serve 150.
        Assert.True(promotion.Activate(Jan15).IsError);
        Assert.True(promotion.Close(PromotionCloseReason.Withdrawn, Jan15).IsError);
    }

    [Fact]
    public void OnlyADraftCanBeReconfigured()
    {
        var promotion = Active();

        // A live campaign's terms are something customers may already have been shown — silently
        // changing the minimum spend under them is Onyx A6's shape (showing one price, charging
        // another) applied to promotions.
        var result = promotion.Reconfigure(
            "Changed terms",
            Discount.Percentage(0.50m).Value,
            ValidityWindow.Create(Jan1, Feb1).Value,
            PromotionScope.EntireBasket,
            StackingPolicy.Exclusive,
            null, null, null, Jan15);

        Assert.True(result.IsError);
        Assert.Equal("Promotion.OnlyDraftsCanBeEdited", result.FirstError.Code);
    }

    // ----------------------------------------------------------------- the window (Onyx M17)

    [Fact]
    public void TheWindowIsHalfOpen_InclusiveStartExclusiveEnd()
    {
        var window = ValidityWindow.Create(Jan1, Feb1).Value;

        // Onyx's comment said "codes remain valid through the end of the listed day" and its
        // code compared DateTime.Now against midnight at the START of that day — so the code
        // died a full day early, in server local time. Stating both instants explicitly, with
        // one documented convention, is what removes the gap between comment and behaviour.
        Assert.True(window.Contains(Jan1));
        Assert.True(window.Contains(Feb1.AddTicks(-1)));
        Assert.False(window.Contains(Feb1));
        Assert.False(window.Contains(Jan1.AddTicks(-1)));
    }

    [Fact]
    public void AWindowThatEndsBeforeItStartsIsRejected()
    {
        Assert.True(ValidityWindow.Create(Feb1, Jan1).IsError);
        Assert.True(ValidityWindow.Create(Jan1, Jan1).IsError);
    }

    [Fact]
    public void EvaluateDistinguishesNotStartedFromExpired()
    {
        var promotion = Active();

        var early = promotion.Evaluate(Usd(100), Usd(100), 0, Jan1.AddDays(-1));
        var late = promotion.Evaluate(Usd(100), Usd(100), 0, Feb1);

        // Both are failures, and both say which. Onyx M15 returned a 0% rate for anything it
        // could not honour and let the order through as though a valid code had been used.
        Assert.Equal("Promotion.NotStartedYet", early.FirstError.Code);
        Assert.Equal("Promotion.Expired", late.FirstError.Code);
    }

    // ----------------------------------------------------------------- the discount arithmetic

    [Fact]
    public void APercentageDiscountIsRoundedToCashHalfUp()
    {
        var promotion = Active(Discount.Percentage(0.15m).Value);

        // 15% of 10.10 is 1.515. Money rounds half-up to 1.52 — once, here, rather than
        // carrying 1.515 into the next step. Onyx cast to double, never rounded, and produced
        // real order totals like 83.096766 (M6, M7).
        var discount = promotion.Evaluate(Usd(10.10m), Usd(10.10m), 0, Jan15);

        Assert.False(discount.IsError);
        Assert.Equal(1.52m, discount.Value.Amount);
    }

    [Fact]
    public void AFixedDiscountIsCappedAtTheBasis()
    {
        var promotion = Active(Discount.Fixed(Usd(50)).Value);

        // Without the cap this is a negative subtotal, which then flows into the shipping
        // threshold and the tax base and produces a total nobody can explain.
        var discount = promotion.Evaluate(Usd(30), Usd(30), 0, Jan15);

        Assert.Equal(30m, discount.Value.Amount);
    }

    [Fact]
    public void AFixedDiscountExactlyEqualToTheBasisIsTheWholeBasis()
    {
        var promotion = Active(Discount.Fixed(Usd(30)).Value);

        var discount = promotion.Evaluate(Usd(30), Usd(30), 0, Jan15);

        Assert.Equal(30m, discount.Value.Amount);
    }

    [Fact]
    public void AFixedDiscountDoesNotApplyToABasketInAnotherCurrency()
    {
        var promotion = Active(Discount.Fixed(Usd(50)).Value);
        var aed = Money.Create(200, "AED").Value;

        // "50 off" does not mean the same thing in two currencies, and converting would need an
        // effective-dated, auditable rate that Country deliberately does not carry.
        var discount = promotion.Evaluate(aed, aed, 0, Jan15);

        Assert.True(discount.IsError);
        Assert.Equal("Promotion.CurrencyMismatch", discount.FirstError.Code);
    }

    [Fact]
    public void APercentageDiscountAppliesToAnyCurrency()
    {
        var promotion = Active(Discount.Percentage(0.10m).Value);
        var aed = Money.Create(200, "AED").Value;

        var discount = promotion.Evaluate(aed, aed, 0, Jan15);

        Assert.False(discount.IsError);
        Assert.Equal(20m, discount.Value.Amount);
        Assert.Equal("AED", discount.Value.CurrencyCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    public void AnImpossibleRateIsRejected(decimal rate) =>
        Assert.True(Discount.Percentage(rate).IsError);

    [Fact]
    public void AHundredPercentDiscountIsAllowed()
    {
        // A free sample is a real campaign, so 100% is permitted. Zero is not, because a
        // promotion worth nothing is a configuration mistake rather than a business decision.
        Assert.False(Discount.Percentage(1.0m).IsError);
        Assert.True(Discount.Percentage(0m).IsError);
        Assert.True(Discount.Fixed(Usd(0)).IsError);
    }

    // ----------------------------------------------------------------- minimum spend

    [Fact]
    public void TheMinimumSpendIsABoundaryAtTheStatedAmount()
    {
        var promotion = Active(minimumSpend: Usd(75));

        // Onyx M9: its volume discount used `> 75` and its free-shipping threshold used `>= 75`,
        // so two rules about the same number disagreed at exactly 75. One comparison, one test
        // at the boundary.
        Assert.True(promotion.Evaluate(Usd(74.99m), Usd(74.99m), 0, Jan15).IsError);
        Assert.False(promotion.Evaluate(Usd(75m), Usd(75m), 0, Jan15).IsError);
        Assert.False(promotion.Evaluate(Usd(75.01m), Usd(75.01m), 0, Jan15).IsError);
    }

    [Fact]
    public void TheMinimumSpendIsTestedAgainstTheWholeSubtotal_NotTheScopedPart()
    {
        var headphones = Guid.NewGuid();
        var promotion = Active(
            minimumSpend: Usd(100),
            scope: PromotionScope.ForProducts([headphones]));

        // "Spend 100, get 20% off headphones" is the normal reading of a minimum spend, so the
        // minimum tests the basket and the discount applies to the scope.
        var discount = promotion.Evaluate(
            merchandiseSubtotal: Usd(120),
            scopedSubtotal: Usd(40),
            customerRedemptionsSoFar: 0,
            nowUtc: Jan15);

        Assert.False(discount.IsError);
        Assert.Equal(4m, discount.Value.Amount);
    }

    // ----------------------------------------------------------------- scope

    [Fact]
    public void AScopedPromotionDiscountsItsOwnLines()
    {
        var headphones = Guid.NewGuid();
        var laptop = Guid.NewGuid();

        var promotion = Active(
            Discount.Percentage(0.20m).Value,
            scope: PromotionScope.ForProducts([headphones]));

        var scoped = promotion.ScopedSubtotal(
            [(headphones, 50m), (laptop, 1000m)], "USD");

        // 20% off headphones must discount the headphones, not a fifth of the laptop. Onyx
        // applied every discount to the whole subtotal because the whole subtotal was the only
        // number it had.
        Assert.Equal(50m, scoped.Amount);

        var discount = promotion.Evaluate(Usd(1050), scoped, 0, Jan15);
        Assert.Equal(10m, discount.Value.Amount);
    }

    [Fact]
    public void AScopedPromotionAgainstABasketWithNothingInScopeFailsWithAReason()
    {
        var promotion = Active(scope: PromotionScope.ForProducts([Guid.NewGuid()]));
        var scoped = promotion.ScopedSubtotal([(Guid.NewGuid(), 100m)], "USD");

        Assert.True(scoped.IsZero);

        // Quoted a reason, not a discount of zero. "Silently zero" is M15.
        var discount = promotion.Evaluate(Usd(100), scoped, 0, Jan15);

        Assert.True(discount.IsError);
        Assert.Equal("Promotion.NotApplicableToBasket", discount.FirstError.Code);
    }

    [Fact]
    public void AnUnscopedPromotionIncludesEverything()
    {
        var promotion = Active();

        var scoped = promotion.ScopedSubtotal(
            [(Guid.NewGuid(), 10m), (Guid.NewGuid(), 20m)], "USD");

        Assert.Equal(30m, scoped.Amount);
    }

    // ----------------------------------------------------------------- caps

    [Fact]
    public void TheTotalCapStopsFurtherRedemptions_AndClosesTheCampaign()
    {
        var promotion = Active(maxRedemptions: 2);

        Assert.False(promotion.Redeem(Guid.NewGuid(), "session-1", 0, Jan15).IsError);
        Assert.False(promotion.Redeem(Guid.NewGuid(), "session-2", 0, Jan15).IsError);

        // Reaching the cap closes the campaign in the same transaction as the redemption that
        // filled it, so the next quote is refused by status rather than by a cap check.
        Assert.True(promotion.Status.IsClosed);
        Assert.Equal(2, promotion.RedemptionCount);

        var third = promotion.Redeem(Guid.NewGuid(), "session-3", 0, Jan15);
        Assert.True(third.IsError);

        var closed = promotion.DomainEvents.OfType<PromotionClosedEvent>().Single();
        Assert.Equal(PromotionCloseReason.Exhausted, closed.Reason);
    }

    [Fact]
    public void ThePerCustomerCapIsEnforcedAndNamesTheLimit()
    {
        var promotion = Active(maxPerCustomer: 1);
        var customer = Guid.NewGuid();

        Assert.False(promotion.Redeem(customer, "session-1", 0, Jan15).IsError);

        // The caller supplies the count, because the aggregate does not query. One use already
        // held means the second is refused.
        var second = promotion.Redeem(customer, "session-2", 1, Jan15);

        Assert.True(second.IsError);
        Assert.Equal("Promotion.CustomerCapReached", second.FirstError.Code);
        Assert.Contains("already used", second.FirstError.Description);
    }

    [Fact]
    public void TheRedemptionLedgerRowCarriesTheCustomerOrdinal()
    {
        var promotion = Active(maxPerCustomer: 3);
        var customer = Guid.NewGuid();

        var first = promotion.Redeem(customer, "session-1", 0, Jan15).Value;
        var second = promotion.Redeem(customer, "session-2", 1, Jan15).Value;

        // The ordinal is what turns "at most N per customer" into a plain unique index — see
        // PromotionRedemptionConfiguration.
        Assert.Equal(1, first.CustomerOrdinal);
        Assert.Equal(2, second.CustomerOrdinal);
    }

    [Fact]
    public void ReleasingAUseGivesTheSlotBack_WithoutReopeningTheCampaign()
    {
        var promotion = Active(maxRedemptions: 1);
        var customer = Guid.NewGuid();

        promotion.Redeem(customer, "session-1", 0, Jan15);
        Assert.True(promotion.Status.IsClosed);

        promotion.ReturnUse(customer, "session-1", ReleaseReason.SessionExpired, Jan15);

        // The slot is returned, so a limited campaign is not permanently short because somebody
        // abandoned a basket — but the campaign does not quietly reopen, because that would make
        // its end date unpredictable. A human decides whether to run it again.
        Assert.Equal(0, promotion.RedemptionCount);
        Assert.True(promotion.Status.IsClosed);
    }

    [Fact]
    public void RedeemingRequiresAReference()
    {
        var promotion = Active();

        Assert.True(promotion.Redeem(Guid.NewGuid(), "", 0, Jan15).IsError);
        Assert.True(promotion.Redeem(Guid.NewGuid(), "   ", 0, Jan15).IsError);
    }

    [Fact]
    public void RedeemReChecksTheWindow_BecauseTimePassesBetweenQuoteAndCheckout()
    {
        var promotion = Active();

        // A quote shown at Jan 15 and confirmed in February must not go through. Evaluate and
        // Redeem both check, because the window between them is real.
        Assert.True(promotion.Redeem(Guid.NewGuid(), "session-1", 0, Feb1).IsError);
    }

    // ----------------------------------------------------------------- configuration validation

    [Fact]
    public void ACapOfZeroIsRejected()
    {
        var withZeroTotal = Promotion.Create(
            PromotionCode.Create("ZERO").Value, "Zero cap",
            Discount.Percentage(0.1m).Value,
            ValidityWindow.Create(Jan1, Feb1).Value,
            PromotionScope.EntireBasket, StackingPolicy.Exclusive,
            null, 0, null, Jan1);

        Assert.True(withZeroTotal.IsError);
        Assert.Equal("Promotion.CapMustBePositive", withZeroTotal.FirstError.Code);
    }

    [Fact]
    public void AFixedDiscountAndAMinimumSpendInDifferentCurrenciesAreRejected()
    {
        var aed = Money.Create(100, "AED").Value;

        var result = Promotion.Create(
            PromotionCode.Create("MIXED").Value, "Mixed currencies",
            Discount.Fixed(Usd(10)).Value,
            ValidityWindow.Create(Jan1, Feb1).Value,
            PromotionScope.EntireBasket, StackingPolicy.Exclusive,
            aed, null, null, Jan1);

        // The campaign would be silently unusable rather than wrong, which is worse — nothing
        // would ever report an error.
        Assert.True(result.IsError);
        Assert.Equal("Promotion.CurrencyMismatch", result.FirstError.Code);
    }

    // ----------------------------------------------------------------- the code

    [Theory]
    [InlineData("welcome10", "WELCOME10")]
    [InlineData("  WELCOME10  ", "WELCOME10")]
    [InlineData("WEL COME10", "WELCOME10")]
    public void TheCodeIsNormalised(string input, string expected) =>
        Assert.Equal(expected, PromotionCode.Create(input).Value.Value);

    [Fact]
    public void TwoSpellingsOfOneCodeAreEqual() =>
        Assert.Equal(
            PromotionCode.Create("welcome10").Value,
            PromotionCode.Create("WELCOME10").Value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AB")]
    [InlineData("-LEADING")]
    [InlineData("HAS_UNDERSCORE")]
    [InlineData("HAS!BANG")]
    public void AnInvalidCodeIsRejected(string? input) =>
        Assert.True(PromotionCode.Create(input).IsError);
}
