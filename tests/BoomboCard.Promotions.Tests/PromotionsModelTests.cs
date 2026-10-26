using BoomboCard.Promotions.Contracts;
using BoomboCard.Promotions.Domain.PromotionAggregate;
using BoomboCard.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Promotions.Tests;

/// <summary>
/// What the database guarantees, asserted against the database.
///
/// <para>
/// Both usage caps in this module are enforced by constraints rather than by application code —
/// a concurrency token for the total, a unique index for the per-customer limit — so these are
/// the tests that prove the caps exist. None of them could be written against the InMemory
/// provider at all (Onyx <b>T6</b>).
/// </para>
/// </summary>
public sealed class PromotionsModelTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static readonly DateTimeOffset Jan1 = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Feb1 = new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan15 = new(2027, 1, 15, 0, 0, 0, TimeSpan.Zero);

    private static Promotion Active(
        string code,
        int? maxRedemptions = null,
        int? maxPerCustomer = null,
        PromotionScope? scope = null)
    {
        var promotion = Promotion.Create(
            PromotionCode.Create(code).Value,
            "A campaign",
            Discount.Percentage(0.10m).Value,
            ValidityWindow.Create(Jan1, Feb1).Value,
            scope ?? PromotionScope.EntireBasket,
            StackingPolicy.Exclusive,
            minimumSpend: null,
            maxRedemptions,
            maxPerCustomer,
            Jan1).Value;

        promotion.Activate(Jan1);
        return promotion;
    }

    // ----------------------------------------------------------------- uniqueness (Onyx M14)

    [Fact]
    public async Task TwoPromotionsCannotShareACode()
    {
        await using var context = fixture.CreateContext();
        context.Promotions.Add(Active("UNIQUE-1"));
        await context.SaveChangesAsync();

        await using var second = fixture.CreateContext();
        second.Promotions.Add(Active("UNIQUE-1"));

        // One unique row holds a code's rate, window and caps together, so there is nothing to
        // diverge from. Onyx kept the expiry in one dictionary and the rate in a separate switch
        // keyed on the same string — and the same code's expiry was written as 2026-12-31 in the
        // pricing service and 2026-11-30 in the nightly report (M14).
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TwoSpellingsOfOneCodeCollide()
    {
        await using var context = fixture.CreateContext();
        context.Promotions.Add(Active("MiXeD-Case"));
        await context.SaveChangesAsync();

        await using var second = fixture.CreateContext();
        second.Promotions.Add(Active("mixed-case"));

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TheCheckoutLookupIsAParameterisedQueryOnTheIndexedColumn()
    {
        await using var context = fixture.CreateContext();
        context.Promotions.Add(Active("LOOKUP-ME"));
        await context.SaveChangesAsync();

        var code = PromotionCode.Create("lookup-me").Value;

        // LINQ against a mapped column. Code is an owned type rather than a value converter
        // precisely so this is translatable — a converter makes `promotion.Code.Value` a CLR
        // member access EF cannot turn into SQL, and the comparison would silently move to the
        // client (BUGS-AND-LESSONS §3).
        var found = await context.Promotions
            .FirstOrDefaultAsync(promotion => promotion.Code.Value == code.Value);

        Assert.NotNull(found);
        Assert.Equal("LOOKUP-ME", found.Code.Value);
    }

    // ----------------------------------------------------------------- the total cap

    [Fact]
    public async Task TwoCheckoutsRacingForTheLastUse_OnlyOneWins()
    {
        await using (var setup = fixture.CreateContext())
        {
            setup.Promotions.Add(Active("LAST-ONE", maxRedemptions: 1));
            await setup.SaveChangesAsync();
        }

        var code = PromotionCode.Create("LAST-ONE").Value;

        // Two independent contexts, the same shape as two concurrent requests.
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        var a = await first.Promotions.FirstAsync(p => p.Code.Value == code.Value);
        var b = await second.Promotions.FirstAsync(p => p.Code.Value == code.Value);

        // Both read RedemptionCount = 0 and both pass the cap check.
        var redemptionA = a.Redeem(Guid.NewGuid(), "session-a", 0, Jan15);
        var redemptionB = b.Redeem(Guid.NewGuid(), "session-b", 0, Jan15);

        Assert.False(redemptionA.IsError);
        Assert.False(redemptionB.IsError);

        first.Redemptions.Add(redemptionA.Value);
        await first.SaveChangesAsync();

        second.Redemptions.Add(redemptionB.Value);

        // The version predicate is what makes exactly one of them win: the loser's UPDATE
        // affects zero rows. In the application ResilientCommandDispatcher retries in a fresh
        // scope, reloads a count of 1, and returns UsageCapReached.
        //
        // Identical in shape to Catalog's oversell prevention. Onyx D1 is the same read-compare-
        // write with no token, where both writers "succeeded" and four units sold from three.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var verify = fixture.CreateContext();
        var stored = await verify.Promotions.FirstAsync(p => p.Code.Value == code.Value);

        Assert.Equal(1, stored.RedemptionCount);
        Assert.True(stored.Status.IsClosed);
        Assert.Equal(1, await verify.Redemptions.CountAsync(r => r.PromotionId == stored.Id));
    }

    // ----------------------------------------------------------------- the per-customer cap

    [Fact]
    public async Task ThePerCustomerCapIsEnforcedByTheUniqueIndex_NotByTheRead()
    {
        Guid promotionId;

        await using (var setup = fixture.CreateContext())
        {
            var promotion = Active("ONE-EACH", maxPerCustomer: 1);
            promotionId = promotion.Id;
            setup.Promotions.Add(promotion);
            await setup.SaveChangesAsync();
        }

        var customer = Guid.NewGuid();

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        var a = await first.Promotions.FirstAsync(p => p.Id == promotionId);
        var b = await second.Promotions.FirstAsync(p => p.Id == promotionId);

        // Both count zero existing uses, so both compute CustomerOrdinal = 1 and both pass the
        // cap check. This is the race the read cannot close.
        first.Redemptions.Add(a.Redeem(customer, "session-a", 0, Jan15).Value);
        second.Redemptions.Add(b.Redeem(customer, "session-b", 0, Jan15).Value);

        await first.SaveChangesAsync();

        // UX_PromotionRedemptions_CustomerOrdinal rejects the second row. A count cannot be a
        // unique index, so the ordinal turns it into one — which is what makes "at most N per
        // customer" a database guarantee rather than an application-level hope, for any N and
        // with no provider-specific SQL. See PromotionRedemptionConfiguration.
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());

        await using var verify = fixture.CreateContext();

        Assert.Equal(
            1,
            await verify.Redemptions.CountAsync(r =>
                r.PromotionId == promotionId && r.CustomerId == customer));
    }

    [Fact]
    public async Task ACapOfMoreThanOnePerCustomerAllowsExactlyThatMany()
    {
        Guid promotionId;

        await using (var setup = fixture.CreateContext())
        {
            var promotion = Active("THREE-EACH", maxPerCustomer: 3);
            promotionId = promotion.Id;
            setup.Promotions.Add(promotion);
            await setup.SaveChangesAsync();
        }

        var customer = Guid.NewGuid();

        for (var use = 0; use < 3; use++)
        {
            await using var context = fixture.CreateContext();
            var promotion = await context.Promotions.FirstAsync(p => p.Id == promotionId);

            var redemption = promotion.Redeem(customer, $"session-{use}", use, Jan15);

            Assert.False(redemption.IsError);
            context.Redemptions.Add(redemption.Value);
            await context.SaveChangesAsync();
        }

        await using var fourth = fixture.CreateContext();
        var last = await fourth.Promotions.FirstAsync(p => p.Id == promotionId);

        // The ordinal generalises — the same index enforces a cap of three as a cap of one,
        // which a filtered unique index on (PromotionId, CustomerId) could not do.
        var refused = last.Redeem(customer, "session-4", 3, Jan15);

        Assert.True(refused.IsError);
        Assert.Equal("Promotion.CustomerCapReached", refused.FirstError.Code);
    }

    // ----------------------------------------------------------------- idempotency on the session

    [Fact]
    public async Task ThePromotionAndSessionPairIsUnique_SoARetriedCheckoutCannotDoubleRedeem()
    {
        Guid promotionId;

        await using (var setup = fixture.CreateContext())
        {
            var promotion = Active("RETRY-SAFE", maxPerCustomer: 5);
            promotionId = promotion.Id;
            setup.Promotions.Add(promotion);
            await setup.SaveChangesAsync();
        }

        var customer = Guid.NewGuid();

        await using (var first = fixture.CreateContext())
        {
            var promotion = await first.Promotions.FirstAsync(p => p.Id == promotionId);
            first.Redemptions.Add(promotion.Redeem(customer, "same-session", 0, Jan15).Value);
            await first.SaveChangesAsync();
        }

        await using var second = fixture.CreateContext();
        var reloaded = await second.Promotions.FirstAsync(p => p.Id == promotionId);
        second.Redemptions.Add(reloaded.Redeem(customer, "same-session", 1, Jan15).Value);

        // UX_PromotionRedemptions_Session. The handler checks for an existing row first and
        // returns success, so this exception is the backstop rather than the normal path — the
        // same division of labour as everywhere else here: the constraint is the guarantee, the
        // read is the courtesy.
        //
        // Onyx M5 is the counter-example: its payment retry sent no idempotency key at all, so a
        // timeout followed by a retry could charge twice, while the PR claimed the gateway call
        // was idempotent (X9).
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    // ----------------------------------------------------------------- the ledger as history

    [Fact]
    public async Task AReleasedRedemptionIsMarkedRatherThanDeleted()
    {
        Guid promotionId;

        await using (var setup = fixture.CreateContext())
        {
            var promotion = Active("RELEASE-ME", maxRedemptions: 10, maxPerCustomer: 5);
            promotionId = promotion.Id;
            setup.Promotions.Add(promotion);
            await setup.SaveChangesAsync();
        }

        var customer = Guid.NewGuid();

        await using (var context = fixture.CreateContext())
        {
            var promotion = await context.Promotions.FirstAsync(p => p.Id == promotionId);
            context.Redemptions.Add(promotion.Redeem(customer, "abandoned", 0, Jan15).Value);
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            var promotion = await context.Promotions.FirstAsync(p => p.Id == promotionId);
            var holding = await context.Redemptions.FirstAsync(r => r.CheckoutSessionId == "abandoned");

            promotion.ReturnUse(customer, "abandoned", ReleaseReason.SessionExpired, Jan15);
            holding.Release(ReleaseReason.SessionExpired, Jan15);

            await context.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContext();
        var promotionAfter = await verify.Promotions.FirstAsync(p => p.Id == promotionId);
        var row = await verify.Redemptions.FirstAsync(r => r.CheckoutSessionId == "abandoned");

        // The slot is back in the count, and the row survives. That is what separates "100
        // people redeemed this" from "140 tried and 40 abandoned" — a distinction Onyx could not
        // make, because a promo code was a nullable string on the order and nothing else
        // (M16, and the campaign-performance gap in ISSUES.md §7).
        Assert.Equal(0, promotionAfter.RedemptionCount);
        Assert.True(row.IsReleased);
        Assert.Equal(ReleaseReason.SessionExpired, row.ReleaseReason);
    }

    // ----------------------------------------------------------------- the rate column

    [Fact]
    public async Task ADiscountRateKeepsFourDecimalPlaces()
    {
        await using (var setup = fixture.CreateContext())
        {
            var promotion = Promotion.Create(
                PromotionCode.Create("SEVEN-HALF").Value,
                "7.5% off",
                Discount.Percentage(0.075m).Value,
                ValidityWindow.Create(Jan1, Feb1).Value,
                PromotionScope.EntireBasket,
                StackingPolicy.Exclusive,
                null, null, null, Jan1).Value;

            setup.Promotions.Add(promotion);
            await setup.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContext();
        var stored = await verify.Promotions.FirstAsync(p => p.Code.Value == "SEVEN-HALF");

        // The model-wide decimal convention is precision(18,2), which is right for money and
        // wrong for a multiplier — it would have rounded 0.075 to 0.08 and quietly turned a 7.5%
        // campaign into an 8% one. The column is configured precision(9,4) for that reason, and
        // this is the test that notices if the convention ever wins. Same class of mistake as
        // the 500-character string convention landing on the outbox payload
        // (BUGS-AND-LESSONS §9).
        Assert.Equal(0.075m, stored.Discount.Rate);

        var discount = stored.Discount.ComputeFor(Money.Create(200m, "USD").Value);
        Assert.Equal(15m, discount.Value.Amount);
    }

    // ----------------------------------------------------------------- the scope collection

    [Fact]
    public async Task AScopedPromotionRoundTripsItsProductIds()
    {
        var headphones = Guid.NewGuid();
        var speakers = Guid.NewGuid();

        await using (var setup = fixture.CreateContext())
        {
            setup.Promotions.Add(Active(
                "AUDIO-20",
                scope: PromotionScope.ForProducts([headphones, speakers])));

            await setup.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContext();
        var stored = await verify.Promotions.FirstAsync(p => p.Code.Value == "AUDIO-20");

        Assert.Equal(PromotionScopeKind.SpecificProducts, stored.Scope.Kind);
        Assert.Equal(2, stored.Scope.ProductIds.Count);
        Assert.True(stored.Scope.Includes(headphones));
        Assert.True(stored.Scope.Includes(speakers));
        Assert.False(stored.Scope.Includes(Guid.NewGuid()));
    }
}
