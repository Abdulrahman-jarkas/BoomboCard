using BoomboCard.Catalog.Domain.Exports;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Data.Repositories;

internal sealed class ProductExportRepository(CatalogDbContext context) : IProductExportRepository
{
    public void Add(ProductExport export) => context.ProductExports.Add(export);

    public Task<ProductExport?> GetByIdAsync(Guid exportId, CancellationToken cancellationToken = default) =>
        context.ProductExports.FirstOrDefaultAsync(export => export.Id == exportId, cancellationToken);

    /// <summary>
    /// Oldest first, and capped. An unbounded fetch here would be the same mistake as Onyx's
    /// unvalidated page size (A28).
    /// </summary>
    public Task<List<ProductExport>> GetRequestedAsync(CancellationToken cancellationToken = default) =>
        context.ProductExports
            .AsNoTracking()
            .Where(export => export.Status == ExportStatus.Requested)
            .OrderBy(export => export.RequestedAtUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

    public Task<List<ProductExport>> GetExpiredAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default) =>
        context.ProductExports
            .Where(export => export.Status == ExportStatus.Ready
                && export.ExpiresAtUtc != null
                && export.ExpiresAtUtc <= asOfUtc)
            .OrderBy(export => export.ExpiresAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
}
