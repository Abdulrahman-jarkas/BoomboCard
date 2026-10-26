using BoomboCard.Loyalty.Domain.Ledger;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Loyalty.Data.Repositories;

internal sealed class PointsRepository(LoyaltyDbContext context) : IPointsRepository
{
    public void Add(PointsAccount account) => context.Accounts.Add(account);

    public void AddEntry(PointsLedgerEntry entry) => context.Ledger.Add(entry);

    public Task<PointsAccount?> GetByCustomerAsync(
        Guid customerId,
        CancellationToken cancellationToken = default) =>
        context.Accounts.FirstOrDefaultAsync(
            account => account.CustomerId == customerId, cancellationToken);

    public Task<PointsAccount?> GetByIdAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        context.Accounts.FirstOrDefaultAsync(account => account.Id == accountId, cancellationToken);

    public Task<bool> AccountExistsAsync(
        Guid customerId,
        CancellationToken cancellationToken = default) =>
        context.Accounts.AnyAsync(account => account.CustomerId == customerId, cancellationToken);

    public Task<bool> EntryExistsAsync(
        Guid accountId,
        string reference,
        PointsReason reason,
        CancellationToken cancellationToken = default) =>
        context.Ledger.AnyAsync(
            entry => entry.AccountId == accountId
                && entry.Reference == reference
                && entry.Reason == reason,
            cancellationToken);

    public async Task<int> PointsEarnedAgainstAsync(
        Guid accountId,
        string reference,
        CancellationToken cancellationToken = default) =>
        await context.Ledger
            .Where(entry => entry.AccountId == accountId
                && entry.Reference == reference
                && entry.Reason == PointsReason.Earned)
            .SumAsync(entry => entry.Delta, cancellationToken);

    public Task<List<PointsLedgerEntry>> GetRedemptionsForSessionAsync(
        string checkoutSessionId,
        CancellationToken cancellationToken = default) =>
        context.Ledger
            .Where(entry => entry.Reference == checkoutSessionId
                && entry.Reason == PointsReason.Redeemed)
            .ToListAsync(cancellationToken);

    public async Task<(int CreditedBeforeCutoff, int ConsumedTotal)> GetExpiryFiguresAsync(
        Guid accountId,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        // Two aggregates, computed by the database. Onyx A19's nightly report materialised every
        // order in the system to produce two numbers.
        var credited = await context.Ledger
            .Where(entry => entry.AccountId == accountId
                && entry.Delta > 0
                && entry.OccurredAtUtc < cutoffUtc)
            .SumAsync(entry => entry.Delta, cancellationToken);

        var consumed = await context.Ledger
            .Where(entry => entry.AccountId == accountId && entry.Delta < 0)
            .SumAsync(entry => entry.Delta, cancellationToken);

        // Consumed deltas are negative; the caller wants a positive quantity.
        return (credited, -consumed);
    }

    public Task<List<Guid>> GetExpiryCandidatesAsync(
        DateTimeOffset cutoffUtc,
        int limit,
        CancellationToken cancellationToken = default) =>
        context.Accounts
            .Where(account => !account.IsClosed
                && account.Balance > 0
                && context.Ledger.Any(entry => entry.AccountId == account.Id
                    && entry.Delta > 0
                    && entry.OccurredAtUtc < cutoffUtc))
            // Ordered because it is capped — an unordered Take would return an arbitrary page and
            // could starve the same accounts pass after pass. That bug was live in Catalog's own
            // jobs (BUGS-AND-LESSONS §8), and the EF warning for it now throws.
            .OrderBy(account => account.OpenedAtUtc)
            .Take(limit)
            .Select(account => account.Id)
            .ToListAsync(cancellationToken);
}
