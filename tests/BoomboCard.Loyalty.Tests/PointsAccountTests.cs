using BoomboCard.Loyalty.Domain.Ledger;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate.Events;
using BoomboCard.SharedKernel;

namespace BoomboCard.Loyalty.Tests;

/// <summary>
/// The <see cref="PointsAccount"/> aggregate's rules. Pure unit tests — no database.
/// </summary>
public sealed class PointsAccountTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static Money Usd(decimal amount) => Money.Create(amount, "USD").Value;

    private static PointsAccount WithBalance(int points)
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        if (points > 0)
        {
            account.Earn(points, "seed", Now);
        }

        return account;
    }

    // ----------------------------------------------------------------- earning (Onyx M12)

    [Fact]
    public void ANewAccountStartsAtZero()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        Assert.Equal(0, account.Balance);
        Assert.False(account.IsClosed);
        Assert.Single(account.DomainEvents.OfType<PointsAccountOpenedEvent>());
    }

    [Fact]
    public void EarningCreditsTheBalanceAndProducesALedgerEntry()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        var entry = account.Earn(49, "order-1", Now);

        // Onyx M12: no code path anywhere increased Customer.LoyaltyPoints, so the scheme had a
        // spend side and no earn side. The only balances that existed were typed into DbSeeder.
        Assert.False(entry.IsError);
        Assert.Equal(49, account.Balance);
        Assert.Equal(49, entry.Value.Delta);
        Assert.Equal(PointsReason.Earned, entry.Value.Reason);
        Assert.Equal("order-1", entry.Value.Reference);
        Assert.Equal(49, entry.Value.BalanceAfter);
    }

    [Fact]
    public void EveryMovementRequiresAReference()
    {
        var account = WithBalance(1000);

        // "Where did my points go?" has an answer only if every movement names its cause —
        // which M11 records as impossible in Onyx, because the number redeemed was not written
        // anywhere at all.
        Assert.True(account.Earn(10, "", Now).IsError);
        Assert.True(account.Redeem(100, "  ", Now).IsError);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.99, 0)]
    [InlineData(1.00, 1)]
    [InlineData(49.99, 49)]
    [InlineData(100.00, 100)]
    public void TheEarnRuleTruncatesRatherThanRounds(decimal spend, int expected)
    {
        // Truncated in the house's favour by at most one point. Rounding up would mean 49.50
        // earns 50, which is fine until somebody notices that splitting an order into halves
        // earns more than placing it once — and gaming a rounding rule is a real thing.
        Assert.Equal(expected, EarnRule.PointsFor(Usd(spend)));
    }

    // ----------------------------------------------------------------- the monetary value (M11)

    [Theory]
    [InlineData(0, 0.00)]
    [InlineData(1, 0.01)]
    [InlineData(100, 1.00)]
    [InlineData(150, 1.50)]
    [InlineData(4999, 49.99)]
    public void PointsConvertToMoneyExactly(int points, decimal expected)
    {
        // 100 points to one unit of currency, so one point is exactly one cent — a rate chosen
        // so this conversion can never round. Onyx M11's whole substance is that its points had
        // no monetary value at all.
        var value = PointsValue.ValueOf(points, "USD");

        Assert.False(value.IsError);
        Assert.Equal(expected, value.Value.Amount);
    }

    [Fact]
    public void TheConversionRoundTrips()
    {
        for (var points = 0; points < 1000; points++)
        {
            var value = PointsValue.ValueOf(points, "USD").Value;
            Assert.Equal(points, PointsValue.PointsFor(value));
        }
    }

    [Fact]
    public void PointsAreWorthTheSameInEverySupportedCurrency()
    {
        // A deliberate simplification of the same kind Country makes about tax: a point is one
        // cent of whatever the basket is priced in. The alternative needs an effective-dated,
        // auditable FX rate this system does not have.
        Assert.Equal(1.00m, PointsValue.ValueOf(100, "USD").Value.Amount);
        Assert.Equal(1.00m, PointsValue.ValueOf(100, "AED").Value.Amount);
    }

    // ----------------------------------------------------------------- redemption (M11, D4)

    [Fact]
    public void RedeemingDebitsTheBalanceAndProducesALedgerEntry()
    {
        var account = WithBalance(1000);

        var entry = account.Redeem(400, "session-1", Now);

        Assert.False(entry.IsError);
        Assert.Equal(600, account.Balance);
        Assert.Equal(-400, entry.Value.Delta);
        Assert.Equal(PointsReason.Redeemed, entry.Value.Reason);
    }

    [Fact]
    public void RedeemingMoreThanTheBalanceIsRefusedBeforeAnythingChanges()
    {
        var account = WithBalance(300);

        // Onyx D4: this check lived inside the debit, which ran AFTER the order and every stock
        // decrement were committed — so it threw an InvalidOperationException that surfaced as a
        // 500 with the order standing. Here it is a validation result from the quote.
        var result = account.Redeem(500, "session-1", Now);

        Assert.True(result.IsError);
        Assert.Equal("Points.Insufficient", result.FirstError.Code);
        Assert.Equal(300, account.Balance);
        Assert.DoesNotContain(account.DomainEvents.OfType<PointsMovedEvent>(), e => e.Delta < 0);
    }

    [Fact]
    public void CanRedeemAndRedeemAgreeOnEveryCase()
    {
        // The same rule, called from both the quote and the debit — so a quote cannot promise a
        // discount the debit then refuses. Onyx M18 is one availability rule written twice, as
        // <= and <, with two screens disagreeing about the same product.
        foreach (var balance in new[] { 0, 50, 100, 500 })
        {
            foreach (var requested in new[] { -1, 0, 1, 99, 100, 500, 501 })
            {
                var forCheck = WithBalance(balance);
                var forDebit = WithBalance(balance);

                var checkFailed = forCheck.CanRedeem(requested).IsError;
                var debitFailed = forDebit.Redeem(requested, "session", Now).IsError;

                Assert.Equal(checkFailed, debitFailed);
            }
        }
    }

    [Fact]
    public void ARedemptionBelowTheMinimumIsRefusedWithItsOwnReason()
    {
        var account = WithBalance(1000);

        var result = account.CanRedeem(PointsValue.MinimumRedemption - 1);

        Assert.True(result.IsError);
        Assert.Equal("Points.BelowMinimumRedemption", result.FirstError.Code);

        Assert.False(account.CanRedeem(PointsValue.MinimumRedemption).IsError);
    }

    [Fact]
    public void TheBalanceCanNeverGoNegative()
    {
        var account = WithBalance(100);

        Assert.True(account.Redeem(101, "session", Now).IsError);
        Assert.Equal(100, account.Balance);

        Assert.False(account.Redeem(100, "session", Now).IsError);
        Assert.Equal(0, account.Balance);

        Assert.True(account.Redeem(100, "session-2", Now).IsError);
        Assert.Equal(0, account.Balance);
    }

    // ----------------------------------------------------------------- returning

    [Fact]
    public void ReturningGivesThePointsBack()
    {
        var account = WithBalance(1000);
        account.Redeem(400, "session-1", Now);

        var entry = account.Return(400, "session-1:returned", Now);

        Assert.False(entry.IsError);
        Assert.Equal(1000, account.Balance);
        Assert.Equal(PointsReason.Returned, entry.Value.Reason);
    }

    // ----------------------------------------------------------------- reversal on refund

    [Fact]
    public void ReversingAnEarningTakesBackWhatItGave()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);
        account.Earn(500, "order-1", Now);

        var entry = account.ReverseEarning(500, "refund-1", Now);

        Assert.False(entry.IsError);
        Assert.Equal(0, account.Balance);
        Assert.Equal(PointsReason.Reversed, entry.Value.Reason);
    }

    [Fact]
    public void AReversalIsClampedToWhatIsLeft()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);
        account.Earn(500, "order-1", Now);
        account.Redeem(400, "session-1", Now);

        // 100 left, 500 to claw back. A negative loyalty balance is a concept this scheme does
        // not have, and inventing one to handle a refund edge case would be worse than absorbing
        // the shortfall.
        var entry = account.ReverseEarning(500, "refund-1", Now);

        Assert.False(entry.IsError);
        Assert.Equal(0, account.Balance);
        Assert.Equal(-100, entry.Value.Delta);
    }

    // ----------------------------------------------------------------- expiry

    [Theory]
    // credited before the cutoff, consumed in total, expected to lapse
    [InlineData(0, 0, 0)]
    [InlineData(500, 0, 500)]
    [InlineData(500, 200, 300)]
    [InlineData(500, 500, 0)]
    [InlineData(500, 700, 0)]
    [InlineData(200, 500, 0)]
    public void ExpirableBeforeIsFifoWithNoLotTracking(
        int creditedBeforeCutoff,
        int consumedTotal,
        int expected)
    {
        // The insight that avoids per-batch tracking: points are consumed oldest-first, so
        // whatever is left is always the most recently credited. The amount still alive from
        // before a cutoff is therefore max(0, creditedBefore - consumedEver) — two indexed
        // sums, no lot entities, nothing mutable on a ledger row.
        Assert.Equal(expected, PointsAccount.ExpirableBefore(creditedBeforeCutoff, consumedTotal));
    }

    [Fact]
    public void ExpiringWritesANegativeEntryRatherThanDeletingTheEarning()
    {
        var account = WithBalance(500);

        var entry = account.Expire(500, "expiry:2028-01-15", Now);

        Assert.False(entry.IsError);
        Assert.Equal(0, account.Balance);
        Assert.Equal(-500, entry.Value.Delta);
        Assert.Equal(PointsReason.Expired, entry.Value.Reason);
    }

    [Fact]
    public void ExpiryIsClampedToTheBalance()
    {
        var account = WithBalance(100);

        // The structural backstop: a miscomputed cutoff can never drive the balance negative.
        var entry = account.Expire(999, "expiry", Now);

        Assert.False(entry.IsError);
        Assert.Equal(0, account.Balance);
        Assert.Equal(-100, entry.Value.Delta);
    }

    // ----------------------------------------------------------------- administration

    [Fact]
    public void AnAdjustmentRequiresAnActor()
    {
        var account = WithBalance(100);

        // Same rule as Customer.ChangeRole: a change to something of monetary value with no
        // attributable author is the audit record Onyx did not have (A40).
        Assert.True(account.Adjust(50, Guid.Empty, "goodwill", Now).IsError);
    }

    [Fact]
    public void AnAdjustmentIsSignedAndRecordsTheActor()
    {
        var account = WithBalance(100);
        var admin = Guid.NewGuid();

        Assert.False(account.Adjust(50, admin, "goodwill", Now).IsError);
        Assert.Equal(150, account.Balance);

        Assert.False(account.Adjust(-50, admin, "correction", Now).IsError);
        Assert.Equal(100, account.Balance);

        var moves = account.DomainEvents.OfType<PointsMovedEvent>()
            .Where(move => move.Reason == PointsReason.Adjusted)
            .ToList();

        Assert.Equal(2, moves.Count);
        Assert.All(moves, move => Assert.Contains(admin.ToString("n"), move.Reference));
    }

    [Fact]
    public void ANegativeAdjustmentIsClampedToTheBalance()
    {
        var account = WithBalance(100);

        Assert.False(account.Adjust(-999, Guid.NewGuid(), "correction", Now).IsError);
        Assert.Equal(0, account.Balance);
    }

    // ----------------------------------------------------------------- closing

    [Fact]
    public void ClosingForfeitsWhatRemainsAndRecordsIt()
    {
        var account = WithBalance(250);

        var entry = account.Close("anonymised", Now);

        Assert.False(entry.IsError);
        Assert.Equal(0, account.Balance);
        Assert.True(account.IsClosed);
        Assert.Equal(PointsReason.Forfeited, entry.Value.Reason);

        var closed = account.DomainEvents.OfType<PointsAccountClosedEvent>().Single();
        Assert.Equal(250, closed.ForfeitedPoints);
    }

    [Fact]
    public void AClosedAccountCannotBeChangedAtAll()
    {
        var account = WithBalance(250);
        account.Close("anonymised", Now);

        // Terminal, like CustomerStatus.Anonymised and for the same reason.
        Assert.True(account.Earn(10, "order", Now).IsError);
        Assert.True(account.Redeem(100, "session", Now).IsError);
        Assert.True(account.Return(10, "session", Now).IsError);
        Assert.True(account.ReverseEarning(10, "refund", Now).IsError);
        Assert.True(account.Expire(10, "expiry", Now).IsError);
        Assert.True(account.Adjust(10, Guid.NewGuid(), "note", Now).IsError);
        Assert.True(account.Close("again", Now).IsError);
        Assert.True(account.CanRedeem(100).IsError);
    }

    [Fact]
    public void ClosingAnEmptyAccountStillWorks()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        var entry = account.Close("anonymised", Now);

        Assert.False(entry.IsError);
        Assert.True(account.IsClosed);
        Assert.Equal(0, entry.Value.Delta);
    }
}
