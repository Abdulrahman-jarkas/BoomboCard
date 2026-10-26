using BoomboCard.Loyalty.Domain.Ledger;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Loyalty.Tests;

/// <summary>
/// What the database guarantees, asserted against the database — and the integrity invariant
/// that makes the balance column trustworthy.
/// </summary>
public sealed class LoyaltyModelTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    // ----------------------------------------------------------------- uniqueness

    [Fact]
    public async Task ACustomerCannotHaveTwoPointsAccounts()
    {
        var customerId = Guid.NewGuid();

        await using var context = fixture.CreateContext();
        context.Accounts.Add(PointsAccount.Open(customerId, Now));
        await context.SaveChangesAsync();

        await using var second = fixture.CreateContext();
        second.Accounts.Add(PointsAccount.Open(customerId, Now));

        // The account is opened by a handler reacting to CustomerRegisteredIntegrationEvent, and
        // that delivery is at-least-once — so the inbox claim is the first guard and this index
        // is the backstop. Onyx D10 had no unique constraint on any natural key.
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TheSameMovementCannotBeRecordedTwice()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        await using (var setup = fixture.CreateContext())
        {
            setup.Accounts.Add(account);
            setup.Ledger.Add(account.Earn(100, "order-1", Now).Value);
            await setup.SaveChangesAsync();
        }

        await using var second = fixture.CreateContext();
        var reloaded = await second.Accounts.FirstAsync(a => a.Id == account.Id);
        second.Ledger.Add(reloaded.Earn(100, "order-1", Now).Value);

        // UX_PointsLedger_Idempotency on (AccountId, Reference, Reason) — the same three-column
        // shape Catalog uses for stock. A redelivered "order paid" credits once.
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task AnEarningAndItsReversalCoexistAgainstTheSameReference()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        await using var context = fixture.CreateContext();
        context.Accounts.Add(account);
        context.Ledger.Add(account.Earn(100, "order-1", Now).Value);
        context.Ledger.Add(account.ReverseEarning(100, "order-1", Now).Value);

        // Reason is part of the idempotency key precisely so this works: one order legitimately
        // produces an Earned entry and, if refunded, a Reversed one against the same reference.
        await context.SaveChangesAsync();

        // Scoped to this account, not just the reference. The fixture is per test CLASS, so
        // other tests in this file also write "order-1" rows — counting on the reference alone
        // made this assertion depend on which tests had run first, which is Onyx T2 ("order-
        // dependent tests via shared mutable state") reproduced in my own suite. It failed with
        // 6 instead of 2 and was right to.
        Assert.Equal(
            2,
            await context.Ledger.CountAsync(e =>
                e.AccountId == account.Id && e.Reference == "order-1"));

        Assert.Equal(0, account.Balance);
    }

    // ----------------------------------------------------------------- the balance invariant

    [Fact]
    public async Task TwoSimultaneousRedemptionsOfTheLastPointsCannotBothSucceed()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        await using (var setup = fixture.CreateContext())
        {
            setup.Accounts.Add(account);
            setup.Ledger.Add(account.Earn(500, "seed", Now).Value);
            await setup.SaveChangesAsync();
        }

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        var a = await first.Accounts.FirstAsync(x => x.Id == account.Id);
        var b = await second.Accounts.FirstAsync(x => x.Id == account.Id);

        // Both read a balance of 500 and both pass the sufficiency check.
        first.Ledger.Add(a.Redeem(500, "session-a", Now).Value);
        second.Ledger.Add(b.Redeem(500, "session-b", Now).Value);

        await first.SaveChangesAsync();

        // The version predicate makes exactly one win. This is WHY the balance is a column under
        // a concurrency token rather than a SUM over the ledger: a SUM cannot be the subject of
        // one, and both writers would have computed 500 and both committed a debit — spending
        // 1,000 points from a balance of 500.
        //
        // Onyx D1 is the same read-compare-write with no token, where both writers "succeeded"
        // and four units sold from three.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var verify = fixture.CreateContext();
        var stored = await verify.Accounts.FirstAsync(x => x.Id == account.Id);

        Assert.Equal(0, stored.Balance);
        Assert.Equal(1, await verify.Ledger.CountAsync(e => e.Reason == PointsReason.Redeemed
            && e.AccountId == account.Id));
    }

    /// <summary>
    /// The invariant <c>PointsIntegrityCheckJob</c> asserts, on a worked example — and the
    /// reason the balance column is trustworthy rather than merely present. Onyx had a counter,
    /// no log, and nothing checking anything.
    /// </summary>
    [Fact]
    public async Task TheLedgerAlwaysSumsToTheBalance()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        await using var context = fixture.CreateContext();
        context.Accounts.Add(account);

        void Record(ErrorOr.ErrorOr<PointsLedgerEntry> entry)
        {
            Assert.False(entry.IsError);
            context.Ledger.Add(entry.Value);
        }

        Record(account.Earn(500, "order-1", Now));
        Record(account.Earn(300, "order-2", Now));
        Record(account.Redeem(400, "session-1", Now));
        Record(account.Return(400, "session-1:returned", Now));
        Record(account.Redeem(200, "session-2", Now));
        Record(account.ReverseEarning(300, "refund-2", Now));
        Record(account.Expire(100, "expiry:2028-01-15", Now));
        Record(account.Adjust(50, Guid.NewGuid(), "goodwill", Now));

        await context.SaveChangesAsync();

        await using var verify = fixture.CreateContext();
        var stored = await verify.Accounts.FirstAsync(x => x.Id == account.Id);
        var ledgerSum = await verify.Ledger
            .Where(e => e.AccountId == account.Id)
            .SumAsync(e => e.Delta);

        // 500 + 300 - 400 + 400 - 200 - 300 - 100 + 50 = 250
        Assert.Equal(250, stored.Balance);
        Assert.Equal(stored.Balance, ledgerSum);
    }

    [Fact]
    public async Task EveryEntryRecordsTheBalanceItLeftBehind()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        await using var context = fixture.CreateContext();
        context.Accounts.Add(account);
        context.Ledger.Add(account.Earn(500, "order-1", Now).Value);
        context.Ledger.Add(account.Redeem(200, "session-1", Now).Value);
        await context.SaveChangesAsync();

        await using var verify = fixture.CreateContext();
        var entries = await verify.Ledger
            .Where(e => e.AccountId == account.Id)
            .OrderBy(e => e.BalanceAfter)
            .ToListAsync();

        // So the log can be audited without replaying it, and a statement line can show the
        // running balance the customer saw.
        Assert.Equal([300, 500], entries.Select(e => e.BalanceAfter));
    }

    // ----------------------------------------------------------------- the expiry figures

    [Fact]
    public async Task TheExpiryFiguresMatchTheFifoCalculation()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);
        var old = Now.AddMonths(-18);
        var recent = Now.AddDays(-1);

        await using (var setup = fixture.CreateContext())
        {
            setup.Accounts.Add(account);
            setup.Ledger.Add(account.Earn(500, "old-order", old).Value);
            setup.Ledger.Add(account.Earn(300, "recent-order", recent).Value);
            setup.Ledger.Add(account.Redeem(200, "session-1", recent).Value);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();
        var repository = new Data.Repositories.PointsRepository(context);

        var cutoff = Now - EarnRule.PointsLifetime;
        var (credited, consumed) = await repository.GetExpiryFiguresAsync(account.Id, cutoff);

        // 500 credited before the cutoff; 200 consumed in total. FIFO means the consumption came
        // off the oldest points, so 300 of the old batch is still alive and lapses.
        Assert.Equal(500, credited);
        Assert.Equal(200, consumed);
        Assert.Equal(300, PointsAccount.ExpirableBefore(credited, consumed));
    }

    [Fact]
    public async Task AnAccountWithNothingOldEnoughIsNotACandidate()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        await using (var setup = fixture.CreateContext())
        {
            setup.Accounts.Add(account);
            setup.Ledger.Add(account.Earn(500, "recent", Now.AddDays(-1)).Value);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();
        var repository = new Data.Repositories.PointsRepository(context);

        var candidates = await repository.GetExpiryCandidatesAsync(
            Now - EarnRule.PointsLifetime, 100);

        Assert.DoesNotContain(account.Id, candidates);
    }

    [Fact]
    public async Task AReturnedRedemptionGetsAFreshLifetime()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);
        var old = Now.AddMonths(-18);

        await using (var setup = fixture.CreateContext())
        {
            setup.Accounts.Add(account);
            setup.Ledger.Add(account.Earn(500, "old-order", old).Value);
            setup.Ledger.Add(account.Redeem(500, "session-1", old).Value);
            // Returned NOW, long after the original earning.
            setup.Ledger.Add(account.Return(500, "session-1:returned", Now).Value);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();
        var repository = new Data.Repositories.PointsRepository(context);

        var cutoff = Now - EarnRule.PointsLifetime;
        var (credited, consumed) = await repository.GetExpiryFiguresAsync(account.Id, cutoff);

        // Credits before the cutoff: the 500 earned 18 months ago. Consumed: the 500 redeemed.
        // So nothing lapses — the returned 500 is dated now and sits outside the cutoff.
        //
        // That is deliberately generous, and the reasoning is in PointsAccount.Return: the
        // customer is being given back points they tried to spend on a checkout that failed for
        // our reasons, and also shortening their life would be charging them for our failure.
        Assert.Equal(500, credited);
        Assert.Equal(500, consumed);
        Assert.Equal(0, PointsAccount.ExpirableBefore(credited, consumed));
        Assert.Equal(500, account.Balance);
    }

    // ----------------------------------------------------------------- the ledger survives

    [Fact]
    public async Task TheLedgerCannotBeCascadeDeletedByRemovingAnAccount()
    {
        var account = PointsAccount.Open(Guid.NewGuid(), Now);

        await using (var setup = fixture.CreateContext())
        {
            setup.Accounts.Add(account);
            setup.Ledger.Add(account.Earn(100, "order-1", Now).Value);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();
        var stored = await context.Accounts.FirstAsync(a => a.Id == account.Id);
        context.Accounts.Remove(stored);

        // DeleteBehavior.Restrict. The ledger is a financial record of value given and taken,
        // and deleting an account must not take its history with it — Onyx D13 cascaded a hard
        // DELETE from a customer into their orders and payments, destroying exactly this.
        //
        // An account is closed rather than deleted anyway; this is the backstop.
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
