using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.StockMovements;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Data.Repositories;

internal sealed class ProductRepository(CatalogDbContext context) : IProductRepository
{
    public void Add(Product product) => context.Products.Add(product);

    public void AddMovement(StockMovement movement) => context.StockMovements.Add(movement);

    public void AddMovements(IEnumerable<StockMovement> movements) =>
        context.StockMovements.AddRange(movements);

    /// <summary>
    /// Tracked, with prices included: the caller mutates this instance and the unit of work
    /// saves it. The Version token is what makes a concurrent write safe.
    /// </summary>
    public Task<Product?> GetByIdAsync(Guid productId, CancellationToken cancellationToken = default) =>
        context.Products
            .Include(product => product.Prices)
            .FirstOrDefaultAsync(product => product.Id == productId, cancellationToken);

    /// <summary>
    /// One round trip for a multi-line checkout. Onyx D2 looped per line with a
    /// SaveChangesAsync inside the loop, so a short line partway through left the earlier
    /// lines' stock already committed against an order it then rejected.
    /// </summary>
    public Task<List<Product>> GetByIdsAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default) =>
        context.Products
            .Include(product => product.Prices)
            .Where(product => productIds.Contains(product.Id))
            .ToListAsync(cancellationToken);

    public Task<bool> SkuExistsAsync(Sku sku, CancellationToken cancellationToken = default) =>
        context.Products.AnyAsync(product => product.Sku.Value == sku.Value, cancellationToken);

    public Task<List<Product>> GetBySkusAsync(
        IReadOnlyCollection<string> skus,
        CancellationToken cancellationToken = default) =>
        context.Products
            .Include(product => product.Prices)
            .Where(product => skus.Contains(product.Sku.Value))
            .ToListAsync(cancellationToken);

    public Task<bool> MovementExistsAsync(
        Guid productId,
        string reference,
        StockReason reason,
        CancellationToken cancellationToken = default) =>
        context.StockMovements.AnyAsync(
            movement => movement.ProductId == productId
                && movement.Reference == reference
                && movement.Reason == reason,
            cancellationToken);

    /// <summary>
    /// The reasons that participate in the netting: a checkout deduction and the three ways it
    /// can be given back. Excludes Receipt, Return, Adjustment and Shrinkage, which reference a
    /// delivery note, an order or a user rather than a checkout session.
    /// </summary>
    private static readonly StockReason[] CheckoutReasons =
    [
        StockReason.Sale,
        StockReason.PaymentFailed,
        StockReason.SessionExpired,
        StockReason.OrderCancelled
    ];

    public async Task<Dictionary<Guid, int>> GetHeldQuantitiesAsync(
        string reference,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        var nets = await context.StockMovements
            .AsNoTracking()
            .Where(movement =>
                movement.Reference == reference
                && productIds.Contains(movement.ProductId)
                && CheckoutReasons.Contains(movement.Reason))
            .GroupBy(movement => movement.ProductId)
            .Select(group => new { ProductId = group.Key, Net = group.Sum(m => m.Delta) })
            .ToListAsync(cancellationToken);

        // Summed by the database, flipped positive here. A product with no movements under this
        // reference is simply absent, which the caller reads as "nothing is held".
        return nets
            .Where(row => row.Net < 0)
            .ToDictionary(row => row.ProductId, row => -row.Net);
    }
}
