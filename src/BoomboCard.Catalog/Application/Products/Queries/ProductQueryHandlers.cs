using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Application.Products.Queries;

internal sealed class GetProductQueryHandler(CatalogDbContext context, TimeProvider timeProvider)
    : IRequestHandler<GetProductQuery, ErrorOr<ProductDetailResponse>>
{
    public async Task<ErrorOr<ProductDetailResponse>> Handle(
        GetProductQuery query,
        CancellationToken cancellationToken)
    {
        var at = timeProvider.GetUtcNow();

        var product = await context.Products
            .AsNoTracking()
            .Where(candidate => candidate.Id == query.ProductId)
            .Select(candidate => new ProductDetailResponse(
                candidate.Id,
                candidate.Sku.Value,
                candidate.Name,
                candidate.Status.Value.ToString(),
                candidate.Stock.Quantity,
                candidate.LowStockThreshold,
                candidate.Stock.Quantity < candidate.LowStockThreshold,
                candidate.CostPrice == null ? null : candidate.CostPrice.Amount,
                candidate.CostPrice == null ? null : candidate.CostPrice.CurrencyCode,
                candidate.Version,
                candidate.Prices
                    .OrderBy(price => price.CountryCode)
                    .ThenByDescending(price => price.EffectiveFrom)
                    .Select(price => new ProductPriceDetail(
                        price.Id,
                        price.CountryCode,
                        price.Amount.CurrencyCode,
                        price.Amount.Amount,
                        price.EffectiveFrom,
                        price.EffectiveTo,
                        price.EffectiveFrom <= at && (price.EffectiveTo == null || price.EffectiveTo > at)))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);

        return product is null ? ProductErrors.NotFound(query.ProductId) : product;
    }
}

internal sealed class SearchProductsQueryHandler(CatalogDbContext context)
    : IRequestHandler<SearchProductsQuery, ErrorOr<ProductPage>>
{
    public async Task<ErrorOr<ProductPage>> Handle(
        SearchProductsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > SearchProductsQuery.MaxLimit)
        {
            return Error.Validation(
                "SearchProducts.InvalidLimit",
                $"Limit must be between 1 and {SearchProductsQuery.MaxLimit}.");
        }

        var products = context.Products.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToUpperInvariant();
            products = products.Where(product =>
                product.Sku.Value.Contains(term) || product.Name.ToUpper().Contains(term));
        }

        if (query.Status is not null)
        {
            products = products.Where(product => product.Status.Value == query.Status);
        }

        if (query.LowStockOnly)
        {
            products = products.Where(product => product.Stock.Quantity < product.LowStockThreshold);
        }

        // The keyset predicate. Sku is unique, so there is no tie to break and no row can be
        // seen twice or skipped, however much the table changes between requests.
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            var cursor = query.Cursor.Trim().ToUpperInvariant();
            products = products.Where(product => string.Compare(product.Sku.Value, cursor) > 0);
        }

        // Fetch one more than asked for: if it comes back, there is another page.
        var page = await products
            .OrderBy(product => product.Sku.Value)
            .Take(query.Limit + 1)
            .Select(product => new ProductSummaryResponse(
                product.Id,
                product.Sku.Value,
                product.Name,
                product.Status.Value.ToString(),
                product.Stock.Quantity,
                product.Stock.Quantity < product.LowStockThreshold))
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > query.Limit;
        var items = hasMore ? page[..query.Limit] : page;

        return new ProductPage(items, hasMore ? items[^1].Sku : null);
    }
}

internal sealed class GetPriceHistoryQueryHandler(CatalogDbContext context, TimeProvider timeProvider)
    : IRequestHandler<GetPriceHistoryQuery, ErrorOr<List<PriceHistoryEntry>>>
{
    public async Task<ErrorOr<List<PriceHistoryEntry>>> Handle(
        GetPriceHistoryQuery query,
        CancellationToken cancellationToken)
    {
        if (query.CountryCode is not null)
        {
            var country = SharedKernel.Country.FromCode(query.CountryCode);

            if (country.IsError)
            {
                return country.Errors;
            }
        }

        if (!await context.Products.AnyAsync(product => product.Id == query.ProductId, cancellationToken))
        {
            return ProductErrors.NotFound(query.ProductId);
        }

        var at = timeProvider.GetUtcNow();
        var countryCode = query.CountryCode?.Trim().ToUpperInvariant();

        return await context.Products
            .AsNoTracking()
            .Where(product => product.Id == query.ProductId)
            .SelectMany(product => product.Prices)
            .Where(price => countryCode == null || price.CountryCode == countryCode)
            .OrderBy(price => price.CountryCode)
            .ThenByDescending(price => price.EffectiveFrom)
            .Select(price => new PriceHistoryEntry(
                price.Id,
                price.CountryCode,
                price.Amount.CurrencyCode,
                price.Amount.Amount,
                price.EffectiveFrom,
                price.EffectiveTo,
                price.EffectiveFrom <= at && (price.EffectiveTo == null || price.EffectiveTo > at)))
            .ToListAsync(cancellationToken);
    }
}
