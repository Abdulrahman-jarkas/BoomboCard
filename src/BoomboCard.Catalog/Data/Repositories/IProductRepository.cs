using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.StockMovements;

namespace BoomboCard.Catalog.Data.Repositories;

/// <summary>
/// Note the absence of SaveChangesAsync. Persisting is the unit of work's job, not the
/// repository's — dome-gym and RiverBooks call SaveChangesAsync inside every repository method
/// (AddGymAsync, UpdateAsync, AddSubscriptionAsync), so a use case touching two aggregates
/// commits twice and nothing can batch or roll back as one.
///
/// Onyx D3 is the same mistake without the abstraction: order creation committed N+2 separate
/// times while ARCHITECTURE.md claimed "a single logical transaction ... call SaveChanges()
/// once at the end".
/// </summary>
internal interface IProductRepository
{
    void Add(Product product);

    void AddMovement(StockMovement movement);

    void AddMovements(IEnumerable<StockMovement> movements);

    Task<Product?> GetByIdAsync(Guid productId, CancellationToken cancellationToken = default);

    Task<List<Product>> GetByIdsAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default);

    Task<bool> SkuExistsAsync(Sku sku, CancellationToken cancellationToken = default);

    /// <summary>
    /// One query for every SKU in an import file. Onyx A26's shape was the opposite: a lookup
    /// per row inside a loop.
    /// </summary>
    Task<List<Product>> GetBySkusAsync(
        IReadOnlyCollection<string> skus,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this exact movement has already been recorded. Lets a handler detect a
    /// redelivered message and no-op instead of relying on the unique index to throw.
    /// </summary>
    Task<bool> MovementExistsAsync(
        Guid productId,
        string reference,
        StockReason reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How much stock is still held against a checkout reference, per product.
    ///
    /// <para>
    /// <c>-SUM(Delta)</c> over the checkout reasons: the sale is negative, any restore is the
    /// matching positive, so a reference that nets to zero has been fully released and one that
    /// never deducted anything returns nothing at all. The same arithmetic
    /// <c>GetUnreconciledStockHoldsQueryHandler</c> uses, so the restore cap and the orphan
    /// sweep cannot disagree.
    /// </para>
    ///
    /// <para>
    /// This is what makes <c>RestoreStockCommandHandler</c> unable to invent stock. See
    /// BUGS-AND-LESSONS section 13.
    /// </para>
    /// </summary>
    Task<Dictionary<Guid, int>> GetHeldQuantitiesAsync(
        string reference,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default);
}
