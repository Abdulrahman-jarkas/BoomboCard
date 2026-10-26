using BoomboCard.Catalog.Domain.ProductAggregate;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Application.Products.Queries;

/*
 * Queries, not commands. None of these implement ITransactionalRequest, so UnitOfWorkBehavior
 * does not wrap them and they get no transaction. dome-gym's middleware opened one for every
 * request including pure reads and held it for the whole request lifetime.
 *
 * All of them project server-side straight into a response record. Onyx A19 materialised an
 * entire table with context.Orders.ToList() and then filtered in memory; A26 ran a COUNT query
 * per row inside a Select over an already-materialised list.
 */

// ----------------------------------------------------------------- get one, full detail

/// <summary>
/// One price and its validity window. <c>IsCurrent</c> marks the single price effective right
/// now; a future-dated price appears with <c>IsCurrent: false</c> until it takes effect, and a
/// superseded one has an <c>EffectiveTo</c>.
/// </summary>
public sealed record ProductPriceDetail(
    Guid PriceId,
    string CountryCode,
    string CurrencyCode,
    decimal Amount,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    bool IsCurrent);

/// <summary>
/// Everything about one product, including every price it has — current, superseded and
/// future-dated. <c>Version</c> is the optimistic concurrency token; it advances on every change
/// inside the aggregate.
/// </summary>
public sealed record ProductDetailResponse(
    Guid ProductId,
    string Sku,
    string Name,
    string Status,
    int StockQuantity,
    int LowStockThreshold,
    bool IsLowStock,
    decimal? CostPriceAmount,
    string? CostPriceCurrency,
    uint Version,
    IReadOnlyList<ProductPriceDetail> Prices);

public sealed record GetProductQuery(Guid ProductId) : IRequest<ErrorOr<ProductDetailResponse>>;

// ----------------------------------------------------------------- search, keyset paged

/// <summary>One product as it appears in a listing.</summary>
public sealed record ProductSummaryResponse(
    Guid ProductId,
    string Sku,
    string Name,
    string Status,
    int StockQuantity,
    bool IsLowStock);

/// <summary>
/// A page of results plus the cursor for the next page. Cursor is null when there are no more.
/// </summary>
public sealed record ProductPage(IReadOnlyList<ProductSummaryResponse> Items, string? NextCursor);

/// <summary>
/// Keyset pagination, not offset.
///
/// Onyx A27: <c>GET /api/orders/search</c> did <c>.Skip(skip).Take(take)</c> with NO
/// <c>OrderBy</c>, so page boundaries were undefined and rows could be repeated or skipped
/// between pages. Its repository had a correctly ordered paging method that was never called.
/// A28: <c>take</c> was unvalidated, so a caller could request the entire table.
///
/// <paramref name="Cursor"/> is the last SKU of the previous page. SKU is unique and sorted,
/// which makes it a perfect keyset key — unlike offset paging, inserts and deletes between
/// requests cannot shift the window.
/// </summary>
public sealed record SearchProductsQuery(
    string? Search = null,
    ProductStatusValue? Status = null,
    bool LowStockOnly = false,
    string? Cursor = null,
    int Limit = 50) : IRequest<ErrorOr<ProductPage>>
{
    public const int MaxLimit = 200;
}

// ----------------------------------------------------------------- price history

/// <summary>
/// One entry in a product's price history. Readable only because a price change closes the old
/// row and opens a new one rather than overwriting it.
/// </summary>
public sealed record PriceHistoryEntry(
    Guid PriceId,
    string CountryCode,
    string CurrencyCode,
    decimal Amount,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    bool IsCurrent);

/// <summary>
/// Readable only because prices are superseded rather than overwritten. Onyx had no price
/// history at all, so "what did this cost in March" was unanswerable — and because its catalogue
/// cache was never invalidated, browsing and ordering disagreed indefinitely after any change
/// (A6).
/// </summary>
public sealed record GetPriceHistoryQuery(Guid ProductId, string? CountryCode = null)
    : IRequest<ErrorOr<List<PriceHistoryEntry>>>;
