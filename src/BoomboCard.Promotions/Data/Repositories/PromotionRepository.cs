using BoomboCard.Promotions.Domain.PromotionAggregate;
using BoomboCard.Promotions.Domain.Redemptions;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Promotions.Data.Repositories;

internal sealed class PromotionRepository(PromotionsDbContext context) : IPromotionRepository
{
    public void Add(Promotion promotion) => context.Promotions.Add(promotion);

    public void AddRedemption(PromotionRedemption redemption) =>
        context.Redemptions.Add(redemption);

    public Task<Promotion?> GetByIdAsync(
        Guid promotionId,
        CancellationToken cancellationToken = default) =>
        context.Promotions
            .FirstOrDefaultAsync(promotion => promotion.Id == promotionId, cancellationToken);

    public Task<Promotion?> GetByCodeAsync(
        PromotionCode code,
        CancellationToken cancellationToken = default) =>
        context.Promotions
            .FirstOrDefaultAsync(promotion => promotion.Code.Value == code.Value, cancellationToken);

    public Task<bool> CodeExistsAsync(
        PromotionCode code,
        CancellationToken cancellationToken = default) =>
        context.Promotions
            .AnyAsync(promotion => promotion.Code.Value == code.Value, cancellationToken);

    public Task<int> CountCustomerRedemptionsAsync(
        Guid promotionId,
        Guid customerId,
        CancellationToken cancellationToken = default) =>
        context.Redemptions
            .CountAsync(
                redemption => redemption.PromotionId == promotionId
                    && redemption.CustomerId == customerId
                    && redemption.ReleasedAtUtc == null,
                cancellationToken);

    public Task<List<PromotionRedemption>> GetSessionRedemptionsAsync(
        string checkoutSessionId,
        CancellationToken cancellationToken = default) =>
        context.Redemptions
            .Where(redemption => redemption.CheckoutSessionId == checkoutSessionId
                && redemption.ReleasedAtUtc == null)
            .ToListAsync(cancellationToken);

    public Task<PromotionRedemption?> GetSessionRedemptionAsync(
        Guid promotionId,
        string checkoutSessionId,
        CancellationToken cancellationToken = default) =>
        context.Redemptions
            .FirstOrDefaultAsync(
                redemption => redemption.PromotionId == promotionId
                    && redemption.CheckoutSessionId == checkoutSessionId,
                cancellationToken);

    public async Task<List<Promotion>> GetByIdsAsync(
        IReadOnlyCollection<Guid> promotionIds,
        CancellationToken cancellationToken = default) =>
        promotionIds.Count == 0
            ? []
            : await context.Promotions
                .Where(promotion => promotionIds.Contains(promotion.Id))
                .ToListAsync(cancellationToken);
}
