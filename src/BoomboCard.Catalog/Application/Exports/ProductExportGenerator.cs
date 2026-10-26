using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Domain.Exports;
using BoomboCard.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Catalog.Application.Exports;

public sealed class ExportOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    private static readonly string DefaultStorageDirectory =
        Path.Combine(Path.GetTempPath(), "boombocard", "exports");

    private readonly string _storageDirectory = DefaultStorageDirectory;

    /// <summary>
    /// Where generated files go. Deliberately configured, and deliberately NOT inside the
    /// application directory: Onyx wrote to <c>AppContext.BaseDirectory/exports</c>, which is
    /// wiped on redeploy — invalidating every filename a client was holding (A38).
    ///
    /// A blank configured value falls back to the default rather than overriding it with
    /// nothing. An empty placeholder in appsettings.json is a realistic thing for someone to
    /// leave behind, and it should not be able to break startup.
    /// </summary>
    public string StorageDirectory
    {
        get => _storageDirectory;
        init => _storageDirectory = string.IsNullOrWhiteSpace(value) ? DefaultStorageDirectory : value;
    }
}

/// <summary>
/// Picks up requested exports and generates them.
///
/// Same four safety properties as the outbox dispatcher, for the same reasons:
/// per-export error handling, a per-iteration error boundary so one bad export cannot stop the
/// loop or the process, a real <see cref="PeriodicTimer"/> rather than
/// <c>Task.Delay</c> drift, and outcomes logged after the work rather than before it.
///
/// Contrast Onyx's StockReconciliationJob, which did
/// <c>Parallel.ForEach(products, p => ReplenishAsync(context, p))</c> over a single shared
/// DbContext from an <c>async void</c> method — so it logged success before any work ran, closed
/// the connection while the work was still using it, and terminated the whole worker process on
/// any exception (D6, D7, A22).
/// </summary>
internal sealed class ProductExportGenerator(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ProductExportGenerator> logger,
    ExportOptions? options = null) : BackgroundService
{
    private readonly ExportOptions _options = options ?? new ExportOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval, timeProvider);

        logger.LogInformation(
            "Product export generator started. Storage: {StorageDirectory}.",
            _options.StorageDirectory);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Inside the boundary, not before the loop. Setup code that throws in
                // ExecuteAsync is outside every try/catch, and under the default
                // BackgroundServiceExceptionBehavior.StopHost it takes the whole host down —
                // which is Onyx A18/A21, and which I reproduced here by leaving an empty
                // StorageDirectory in appsettings.json. CreateDirectory is idempotent, so doing
                // it per pass costs nothing and is also self-healing if the directory is removed.
                Directory.CreateDirectory(_options.StorageDirectory);

                await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Export generation pass failed. Continuing on the next tick.");
            }
        }
    }

    private async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var exports = scope.ServiceProvider.GetRequiredService<IProductExportRepository>();

        var pending = await exports.GetRequestedAsync(cancellationToken);

        foreach (var export in pending)
        {
            await GenerateOneAsync(export.Id, cancellationToken);
        }
    }

    /// <summary>
    /// Each export gets its own scope, so one failure cannot poison another's change tracker,
    /// and the claim/generate/complete steps are three separate transactions.
    /// </summary>
    private async Task GenerateOneAsync(Guid exportId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var exports = scope.ServiceProvider.GetRequiredService<IProductExportRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork<CatalogDbContext>>();
        var context = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var export = await exports.GetByIdAsync(exportId, cancellationToken);

        if (export is null)
        {
            return;
        }

        // Claim it. The Version concurrency token makes this atomic, so two generator instances
        // cannot both pick up the same export — the loser gets DbUpdateConcurrencyException.
        var claim = export.BeginGenerating(timeProvider.GetUtcNow());

        if (claim.IsError)
        {
            return;
        }

        try
        {
            // SaveAndDispatchAsync, not SaveChangesAsync: this is a background job, so nothing
            // has opened a transaction and nothing would drain the events BeginGenerating
            // raised. See IUnitOfWork.SaveChangesAsync for the guard that now makes the wrong
            // call throw instead of silently dropping them.
            await unitOfWork.SaveAndDispatchAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogDebug("Export {ExportId} was claimed by another generator. Skipping.", exportId);
            return;
        }

        var fileName = $"products-{export.RequestedAtUtc:yyyyMMdd-HHmmss}-{export.Id:N}.csv";
        var storagePath = Path.Combine(_options.StorageDirectory, fileName);

        try
        {
            int rowCount;

            await using (var file = File.Create(storagePath))
            {
                rowCount = await ProductCsvWriter.WriteAsync(file, StreamRowsAsync(context, cancellationToken), cancellationToken);
            }

            var completed = export.Complete(fileName, storagePath, rowCount, timeProvider.GetUtcNow());

            if (completed.IsError)
            {
                logger.LogError("Could not complete export {ExportId}: {Error}", exportId, completed.FirstError.Description);
                return;
            }

            await unitOfWork.SaveAndDispatchAsync(cancellationToken);

            logger.LogInformation(
                "Export {ExportId} complete: {RowCount} rows written to {FileName}.",
                exportId,
                rowCount,
                fileName);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Export {ExportId} failed.", exportId);

            // Record the failure on the aggregate so the client sees a reason instead of an
            // export stuck in Generating forever.
            export.Fail(exception.Message, timeProvider.GetUtcNow());
            await unitOfWork.SaveAndDispatchAsync(CancellationToken.None);

            TryDeletePartialFile(storagePath);
        }
    }

    /// <summary>
    /// Streamed, not materialised. Onyx A19 did <c>context.Orders.ToList()</c> and filtered in
    /// memory; an export that loads the whole catalogue into a list has the same shape.
    /// </summary>
    private static async IAsyncEnumerable<ProductExportRow> StreamRowsAsync(
        CatalogDbContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = context.Products
            .AsNoTracking()
            .OrderBy(product => product.Sku.Value)
            .Select(product => new
            {
                Sku = product.Sku.Value,
                product.Name,
                Status = product.Status.Value.ToString(),
                product.Stock.Quantity,
                product.LowStockThreshold,
                CostPrice = product.CostPrice == null ? (decimal?)null : product.CostPrice.Amount,
                CostPriceCurrency = product.CostPrice == null ? null : product.CostPrice.CurrencyCode,
                Prices = product.Prices
                    .Where(price => price.EffectiveTo == null)
                    .Select(price => new { price.CountryCode, price.Amount.Amount })
                    .ToList()
            })
            .AsAsyncEnumerable();

        await foreach (var row in query.WithCancellation(cancellationToken))
        {
            yield return new ProductExportRow
            {
                Sku = row.Sku,
                Name = row.Name,
                Status = row.Status,
                StockQuantity = row.Quantity,
                LowStockThreshold = row.LowStockThreshold,
                CostPrice = row.CostPrice,
                CostPriceCurrency = row.CostPriceCurrency,
                PricesByCountry = row.Prices.ToDictionary(
                    price => price.CountryCode,
                    price => (decimal?)price.Amount)
            };
        }
    }

    private void TryDeletePartialFile(string storagePath)
    {
        try
        {
            if (File.Exists(storagePath))
            {
                File.Delete(storagePath);
            }
        }
        catch (IOException exception)
        {
            // Not fatal — the reaper will get it. Logged rather than swallowed, because Onyx
            // A16's empty catch blocks are how its payment failures became invisible.
            logger.LogWarning(exception, "Could not delete partial export file {StoragePath}.", storagePath);
        }
    }
}
