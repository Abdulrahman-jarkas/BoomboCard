using BoomboCard.Loyalty.Domain.Ledger;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate;

namespace BoomboCard.Loyalty.Data.Repositories;

/// <summary>
/// Note the absence of <c>SaveChangesAsync</c>. Persisting is the unit of work's job.
/// </summary>
internal interface IPointsRepository
{
    void Add(PointsAccount account);

    void AddEntry(PointsLedgerEntry entry);

    Task<PointsAccount?> GetByCustomerAsync(
        Guid customerId,
        CancellationToken cancellationToken = default);

    Task<PointsAccount?> GetByIdAsync(Guid accountId, CancellationToken cancellationToken = default);

    Task<bool> AccountExistsAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this exact movement has already been recorded, so a handler can detect a
    /// redelivered message and no-op instead of relying on the unique index to throw.
    /// </summary>
    Task<bool> EntryExistsAsync(
        Guid accountId,
        string reference,
        PointsReason reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The points an earning against this reference actually granted, so a refund reversal is
    /// bounded by what was given rather than by what the caller claims.
    /// </summary>
    Task<int> PointsEarnedAgainstAsync(
        Guid accountId,
        string reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live redemptions held by one checkout session, across accounts — the release path is told
    /// a session id and nothing else.
    /// </summary>
    Task<List<PointsLedgerEntry>> GetRedemptionsForSessionAsync(
        string checkoutSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The two sums the expiry calculation needs: everything credited strictly before the
    /// cutoff, and everything ever consumed. See <c>PointsAccount.ExpirableBefore</c> for why
    /// those two numbers are sufficient and no per-batch tracking is required.
    /// </summary>
    Task<(int CreditedBeforeCutoff, int ConsumedTotal)> GetExpiryFiguresAsync(
        Guid accountId,
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accounts with a non-zero balance holding credits older than the cutoff — the candidates
    /// the expiry job then computes figures for. Ordered and capped.
    /// </summary>
    Task<List<Guid>> GetExpiryCandidatesAsync(
        DateTimeOffset cutoffUtc,
        int limit,
        CancellationToken cancellationToken = default);
}
