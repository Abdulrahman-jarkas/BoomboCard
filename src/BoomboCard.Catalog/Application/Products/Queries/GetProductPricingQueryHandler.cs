using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Application.Products.Queries;

/// <summary>
/// The read Sales uses to build a quote.
///
/// Reads the live row. There is no cache, deliberately: Onyx put a singleton ProductCache
/// holding a scoped DbContext in front of this exact path (A1), disabled the DI scope
/// validator to allow it (A2), never invalidated it (A3), wrote to its dictionary outside its
/// own lock (A4), and shipped a "warmer" that no-opped after its first run (A5). The result was
/// that browsing and ordering read different prices indefinitely (A6) — for a five-product
/// catalogue served by an indexed primary-key lookup.
///
/// A cache goes in when a measurement asks for one, behind IMemoryCache with a TTL, invalidated
/// by ProductPriceChanged.
/// </summary>
internal sealed class GetProductPricingQueryHandler(CatalogDbContext context, TimeProvider timeProvider)
    : IRequestHandler<GetProductPricingQuery, ErrorOr<List<ProductPricingResponse>>>
{
    public async Task<ErrorOr<List<ProductPricingResponse>>> Handle(
        GetProductPricingQuery query,
        CancellationToken cancellationToken)
    {
        var country = Country.FromCode(query.CountryCode);

        if (country.IsError)
        {
            return country.Errors;
        }

        if (query.ProductIds.Count == 0)
        {
            return new List<ProductPricingResponse>();
        }

        var at = timeProvider.GetUtcNow();
        var countryCode = country.Value.Code;

        // Projected server-side. Onyx A19/A26: its nightly report did
        // context.Orders.ToList() then filtered in memory, and its customer list ran a COUNT
        // per row inside a Select over a materialised list.
        return await context.Products
            .AsNoTracking()
            .Where(product => query.ProductIds.Contains(product.Id))
            .SelectMany(
                product => product.Prices.Where(price =>
                    price.CountryCode == countryCode
                    && price.EffectiveFrom <= at
                    && (price.EffectiveTo == null || price.EffectiveTo > at)),
                (product, price) => new ProductPricingResponse(
                    product.Id,
                    product.Sku.Value,
                    product.Name,
                    price.Id,
                    price.Amount.Amount,
                    price.Amount.CurrencyCode,
                    product.Stock.Quantity,
                    product.Status.Value == Domain.ProductAggregate.ProductStatusValue.Active))
            .ToListAsync(cancellationToken);
    }
}
