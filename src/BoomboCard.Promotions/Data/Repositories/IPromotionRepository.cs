using BoomboCard.Promotions.Domain.PromotionAggregate;
using BoomboCard.Promotions.Domain.Redemptions;

namespace BoomboCard.Promotions.Data.Repositories;

/// <summary>
/// Note the absence of <c>SaveChangesAsync</c>. Persisting is the unit of work's job — Onyx D3
/// committed order creation N+2 separate times while <c>ARCHITECTURE.md</c> claimed a single
/// logical transaction (X1).
/// </summary>
internal interface IPromotionRepository
{
    void Add(Promotion promotion);

    void AddRedemption(PromotionRedemption redemption);

    Task<Promotion?> GetByIdAsync(Guid promotionId, CancellationToken cancellationToken = default);

    /// <summary>The checkout lookup. A parameterised equality match on <c>UX_Promotions_Code</c>.</summary>
    Task<Promotion?> GetByCodeAsync(
        PromotionCode code,
        CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(PromotionCode code, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many live uses this customer has of this promotion — releases excluded, because a
    /// released use is one they no longer hold.
    ///
    /// <para>
    /// Counted in SQL. The count feeds the per-customer cap check AND the next row's
    /// <c>CustomerOrdinal</c>, so the unique index catches the race this read cannot.
    /// </para>
    /// </summary>
    Task<int> CountCustomerRedemptionsAsync(
        Guid promotionId,
        Guid customerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The live redemptions held by one checkout session, across every promotion.
    ///
    /// Used by the release path, which is told only a session id, and by the stacking check,
    /// which needs to know what else the session already holds.
    /// </summary>
    Task<List<PromotionRedemption>> GetSessionRedemptionsAsync(
        string checkoutSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this promotion already holds a redemption for this session, so a retried
    /// reservation can no-op instead of relying on the unique index to throw.
    /// </summary>
    Task<PromotionRedemption?> GetSessionRedemptionAsync(
        Guid promotionId,
        string checkoutSessionId,
        CancellationToken cancellationToken = default);

    Task<List<Promotion>> GetByIdsAsync(
        IReadOnlyCollection<Guid> promotionIds,
        CancellationToken cancellationToken = default);
}
