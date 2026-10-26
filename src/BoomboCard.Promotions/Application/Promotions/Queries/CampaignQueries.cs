using BoomboCard.Promotions.Data;
using BoomboCard.Promotions.Domain.PromotionAggregate;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Promotions.Application.Promotions.Queries;

/*
 * Queries. None implement IPromotionsRequest, so no transaction is opened for them.
 *
 * Every one projects into an explicit response record in SQL. No aggregate is reachable from
 * any of them — the rule that makes Onyx S11 (an entity serialised straight to the wire)
 * structurally impossible rather than merely avoided.
 */

/// <summary>The full configuration of one campaign, for the merchandiser who owns it.</summary>
public sealed record PromotionDetailResponse(
    Guid PromotionId,
    string Code,
    string Description,
    string Status,
    string DiscountKind,
    decimal? DiscountRate,
    decimal? DiscountAmount,
    string? DiscountCurrency,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    decimal? MinimumSpend,
    string? MinimumSpendCurrency,
    int? MaxRedemptions,
    int? MaxRedemptionsPerCustomer,
    int RedemptionCount,
    string Stacking,
    string ScopeKind,
    IReadOnlyList<Guid> ScopedProductIds,
    DateTimeOffset CreatedAtUtc,
    uint Version);

public sealed record GetPromotionQuery(Guid PromotionId)
    : IRequest<ErrorOr<PromotionDetailResponse>>;

/// <summary>One row of the campaign list.</summary>
public sealed record PromotionSummaryResponse(
    Guid PromotionId,
    string Code,
    string Description,
    string Status,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int RedemptionCount,
    int? MaxRedemptions);

/// <summary>
/// A page of campaigns, plus the cursor for the next one. Keyset paged over code, which is
/// unique and ordered, so no row can be seen twice or skipped however much the table changes
/// between requests (Onyx A27).
/// </summary>
public sealed record PromotionPage(
    IReadOnlyList<PromotionSummaryResponse> Items,
    string? NextCursor);

/// <summary>
/// Filters, plus the keyset cursor.
///
/// <c>Cursor</c> is the previous page's last code, or null for the first page. <c>Limit</c> is
/// capped by the handler — Onyx A28 left its page size unbounded.
/// </summary>
public sealed record SearchPromotionsQuery(
    string? Search = null,
    PromotionStatusValue? Status = null,
    string? Cursor = null,
    int Limit = 50) : IRequest<ErrorOr<PromotionPage>>;

/// <summary>
/// How a campaign actually performed.
///
/// <para>
/// <c>ISSUES.md</c> §7 lists "campaign performance reporting" as a capability that does not
/// exist, and Onyx could not have built it: a promo code was a nullable string on the order and
/// nothing recorded whether it had been applied, abandoned or refunded. Its nightly report
/// counted orders and divided by a count it never checked for zero (<b>A18</b>), and its
/// promo-audit endpoint handed a user-supplied wildcard to <c>EF.Functions.Like</c> from inside a
/// domain service (<b>A11</b>).
/// </para>
///
/// <para>
/// The ledger makes the three numbers that matter separable: uses still held, uses given back,
/// and how many distinct customers. <c>Released</c> is the one Onyx could never have produced —
/// it is the difference between "100 people used this" and "140 tried and 40 abandoned".
/// </para>
/// </summary>
public sealed record PromotionPerformanceResponse(
    Guid PromotionId,
    string Code,
    string Status,
    int LiveRedemptions,
    int ReleasedRedemptions,
    int DistinctCustomers,
    int? MaxRedemptions,
    DateTimeOffset? FirstRedeemedAtUtc,
    DateTimeOffset? LastRedeemedAtUtc);

public sealed record GetPromotionPerformanceQuery(Guid PromotionId)
    : IRequest<ErrorOr<PromotionPerformanceResponse>>;

internal sealed class GetPromotionQueryHandler(PromotionsDbContext context)
    : IRequestHandler<GetPromotionQuery, ErrorOr<PromotionDetailResponse>>
{
    public async Task<ErrorOr<PromotionDetailResponse>> Handle(
        GetPromotionQuery query,
        CancellationToken cancellationToken)
    {
        var promotion = await context.Promotions
            .AsNoTracking()
            .Where(candidate => candidate.Id == query.PromotionId)
            .Select(candidate => new PromotionDetailResponse(
                candidate.Id,
                candidate.Code.Value,
                candidate.Description,
                candidate.Status.Value.ToString(),
                candidate.Discount.Kind.ToString(),
                candidate.Discount.Kind == DiscountKind.Percentage ? candidate.Discount.Rate : null,
                candidate.Discount.Amount == null ? null : candidate.Discount.Amount.Amount,
                candidate.Discount.Amount == null ? null : candidate.Discount.Amount.CurrencyCode,
                candidate.Window.StartsAtUtc,
                candidate.Window.EndsAtUtc,
                candidate.MinimumSpend == null ? null : candidate.MinimumSpend.Amount,
                candidate.MinimumSpend == null ? null : candidate.MinimumSpend.CurrencyCode,
                candidate.MaxRedemptions,
                candidate.MaxRedemptionsPerCustomer,
                candidate.RedemptionCount,
                candidate.Stacking.ToString(),
                candidate.Scope.Kind.ToString(),
                candidate.Scope.ProductIds.ToList(),
                candidate.CreatedAtUtc,
                candidate.Version))
            .FirstOrDefaultAsync(cancellationToken);

        return promotion is null ? PromotionErrors.NotFound(query.PromotionId) : promotion;
    }
}

internal sealed class SearchPromotionsQueryHandler(PromotionsDbContext context)
    : IRequestHandler<SearchPromotionsQuery, ErrorOr<PromotionPage>>
{
    private const int MaxLimit = 200;

    public async Task<ErrorOr<PromotionPage>> Handle(
        SearchPromotionsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            return Error.Validation(
                "SearchPromotions.InvalidLimit",
                $"Limit must be between 1 and {MaxLimit}.");
        }

        var promotions = context.Promotions.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Upper-cased to match the stored normalised code, so this stays a predicate against
            // a real column. Note what this is NOT: Onyx A11's promo audit took a caller-supplied
            // pattern straight into EF.Functions.Like from a domain service, so a client could
            // send "%" and table-scan every order in the system.
            var term = query.Search.Trim().ToUpperInvariant();

            promotions = promotions.Where(promotion =>
                promotion.Code.Value.Contains(term)
                || promotion.Description.ToUpper().Contains(term));
        }

        if (query.Status is not null)
        {
            promotions = promotions.Where(promotion => promotion.Status.Value == query.Status);
        }

        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            var cursor = query.Cursor.Trim().ToUpperInvariant();
            promotions = promotions.Where(promotion =>
                string.Compare(promotion.Code.Value, cursor) > 0);
        }

        var page = await promotions
            .OrderBy(promotion => promotion.Code.Value)
            .Take(query.Limit + 1)
            .Select(promotion => new PromotionSummaryResponse(
                promotion.Id,
                promotion.Code.Value,
                promotion.Description,
                promotion.Status.Value.ToString(),
                promotion.Window.StartsAtUtc,
                promotion.Window.EndsAtUtc,
                promotion.RedemptionCount,
                promotion.MaxRedemptions))
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > query.Limit;
        var items = hasMore ? page[..query.Limit] : page;

        return new PromotionPage(items, hasMore ? items[^1].Code : null);
    }
}

internal sealed class GetPromotionPerformanceQueryHandler(PromotionsDbContext context)
    : IRequestHandler<GetPromotionPerformanceQuery, ErrorOr<PromotionPerformanceResponse>>
{
    public async Task<ErrorOr<PromotionPerformanceResponse>> Handle(
        GetPromotionPerformanceQuery query,
        CancellationToken cancellationToken)
    {
        var promotion = await context.Promotions
            .AsNoTracking()
            .Where(candidate => candidate.Id == query.PromotionId)
            .Select(candidate => new
            {
                candidate.Id,
                Code = candidate.Code.Value,
                Status = candidate.Status.Value.ToString(),
                candidate.MaxRedemptions
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (promotion is null)
        {
            return PromotionErrors.NotFound(query.PromotionId);
        }

        // Aggregated in SQL — one round trip, computed by the database. Onyx A19's nightly report
        // loaded every order in the system into memory to compute two numbers.
        var stats = await context.Redemptions
            .AsNoTracking()
            .Where(redemption => redemption.PromotionId == query.PromotionId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Live = group.Count(redemption => redemption.ReleasedAtUtc == null),
                Released = group.Count(redemption => redemption.ReleasedAtUtc != null),
                DistinctCustomers = group.Select(redemption => redemption.CustomerId).Distinct().Count(),
                First = group.Min(redemption => (DateTimeOffset?)redemption.RedeemedAtUtc),
                Last = group.Max(redemption => (DateTimeOffset?)redemption.RedeemedAtUtc)
            })
            .FirstOrDefaultAsync(cancellationToken);

        // The empty case, guarded. A campaign nobody has used yet is the normal state of a
        // campaign on its first day, and Onyx A18 is what happens when a report assumes
        // otherwise: an unguarded division by a count of zero threw DivideByZeroException,
        // which under the default BackgroundServiceExceptionBehavior terminated the entire
        // worker process and took the unrelated stock job down with it.
        return new PromotionPerformanceResponse(
            promotion.Id,
            promotion.Code,
            promotion.Status,
            stats?.Live ?? 0,
            stats?.Released ?? 0,
            stats?.DistinctCustomers ?? 0,
            promotion.MaxRedemptions,
            stats?.First,
            stats?.Last);
    }
}
