using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Domain.Exports;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Catalog.Application.Reconciliation;

/// <summary>
/// Housekeeping for exports. Two jobs in one pass, both of which exist because of a specific
/// Onyx failure:
///
///  1. <b>Expire and delete old exports.</b> Onyx A38 wrote export files into
///     <c>AppContext.BaseDirectory/exports</c> and never cleaned them up — unbounded disk growth
///     inside the deployment directory, which is also wiped on redeploy, invalidating every
///     filename a client was holding.
///  2. <b>Recover stuck exports.</b> If the generator dies mid-write, the export sits in
///     <c>Generating</c> forever and the user polls a status that will never change. Marking it
///     Failed makes it visible and retryable. Onyx's equivalent failure mode — a job that died
///     leaving no trace — was A21, where nothing restarted the worker and nothing recorded that
///     it had stopped.
/// </summary>
internal sealed class ExportMaintenanceJob(
    JobRunner<CatalogDbContext> runner,
    TimeProvider timeProvider,
    ILogger<ExportMaintenanceJob> logger,
    ReconciliationOptions? options = null) : BackgroundService
{
    public const string JobName = "catalog.export-maintenance";

    private readonly ReconciliationOptions _options = options ?? new ReconciliationOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.ExportMaintenanceInterval, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, MaintainAsync, stoppingToken);
        }
    }

    private async Task<JobResult> MaintainAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<CatalogDbContext>();
        var exports = services.GetRequiredService<IProductExportRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork<CatalogDbContext>>();

        var nowUtc = timeProvider.GetUtcNow();
        var processed = 0;
        var failed = 0;

        // ---- expired exports: mark Expired, then delete the file

        foreach (var export in await exports.GetExpiredAsync(nowUtc, cancellationToken))
        {
            var path = export.StoragePath;
            var expired = export.Expire(nowUtc);

            if (expired.IsError)
            {
                failed++;
                continue;
            }

            if (TryDelete(path))
            {
                processed++;
            }
            else
            {
                // The row is still expired and no longer downloadable, which is the part that
                // matters. A leftover file is a disk problem, not a correctness problem.
                failed++;
            }
        }

        // ---- exports abandoned mid-generation

        var stuckBefore = nowUtc - _options.StuckExportThreshold;

        var stuck = await context.ProductExports
            .Where(export => export.Status == ExportStatus.Generating
                && export.RequestedAtUtc <= stuckBefore)
            // Oldest first, because it is capped: an unordered Take can return a different
            // arbitrary 50 every pass, so the export that has been stuck longest is not
            // guaranteed to be the one dealt with. Same rule as every other batch query here.
            .OrderBy(export => export.RequestedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var export in stuck)
        {
            logger.LogWarning(
                "Export {ExportId} has been Generating since {RequestedAt} and is assumed abandoned.",
                export.Id,
                export.RequestedAtUtc);

            export.Fail(
                $"Generation did not complete within {_options.StuckExportThreshold}. "
                + "The generator process most likely stopped. Request a new export.",
                nowUtc);

            processed++;
        }

        if (processed > 0 || failed > 0)
        {
            // A job, so no transaction is open and nothing would drain the events the
            // expiry and failure transitions raise. See IUnitOfWork.SaveChangesAsync.
            await unitOfWork.SaveAndDispatchAsync(cancellationToken);
        }

        return new JobResult(processed, failed);
    }

    private bool TryDelete(string? storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return true;
        }

        try
        {
            if (File.Exists(storagePath))
            {
                File.Delete(storagePath);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete expired export file {StoragePath}.", storagePath);
            return false;
        }
    }
}
