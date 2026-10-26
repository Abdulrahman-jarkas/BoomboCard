using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Application.Products.Queries;

internal sealed class BulkStockCheckQueryHandler(IProductRepository products)
    : IRequestHandler<BulkStockCheckQuery, ErrorOr<List<StockCheckResult>>>
{
    public async Task<ErrorOr<List<StockCheckResult>>> Handle(
        BulkStockCheckQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Lines.Count == 0)
        {
            return Error.Validation("BulkStockCheck.NoLines", "At least one line is required.");
        }

        var requested = query.Lines
            .GroupBy(line => line.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));

        // One round trip for every line. Onyx called Products.Find(...) in a loop, once per line.
        var loaded = await products.GetByIdsAsync(requested.Keys.ToList(), cancellationToken);
        var byId = loaded.ToDictionary(product => product.Id);

        return requested
            .Select(entry =>
            {
                if (!byId.TryGetValue(entry.Key, out var product))
                {
                    // Onyx returned `insufficient: false` for a product that did not exist,
                    // because `product is not null && IsInsufficient(...)` short-circuits to
                    // false. A missing product read as "plenty in stock".
                    return new StockCheckResult(entry.Key, string.Empty, entry.Value, 0, false, false, false);
                }

                return new StockCheckResult(
                    product.Id,
                    product.Sku.Value,
                    entry.Value,
                    product.Stock.Quantity,
                    product.CanFulfil(entry.Value),   // the single availability rule
                    true,
                    product.Status.IsActive);
            })
            .ToList();
    }
}
