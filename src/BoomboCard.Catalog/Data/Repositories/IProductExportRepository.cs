using BoomboCard.Catalog.Domain.Exports;

namespace BoomboCard.Catalog.Data.Repositories;

internal interface IProductExportRepository
{
    void Add(ProductExport export);

    Task<ProductExport?> GetByIdAsync(Guid exportId, CancellationToken cancellationToken = default);

    Task<List<ProductExport>> GetRequestedAsync(CancellationToken cancellationToken = default);

    Task<List<ProductExport>> GetExpiredAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default);
}
