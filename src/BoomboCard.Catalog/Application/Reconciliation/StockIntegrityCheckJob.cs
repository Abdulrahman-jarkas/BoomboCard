using BoomboCard.Catalog.Data;
using BoomboCard.SharedKernel.Persistence.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Catalog.Application.Reconciliation;

public sealed class ReconciliationOptions
{
    public TimeSpan StockIntegrityInterval { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan ExportMaintenanceInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long an export may sit in Generating before it is assumed abandoned. Longer than any
    /// realistic generation, short enough that a user is not left waiting forever.
    /// </summary>
    public TimeSpan StuckExportThreshold { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Asserts the invariant that makes the movement log trustworthy:
/// <c>SUM(StockMovement.Delta) == Product.StockQuantity</c>, for every product.
///
/// <para>
/// <b>This is the check Onyx never had, and its absence is why its worst data bug was
/// invisible.</b> Its replenishment job (D5) found anything under 5 in stock and added 50 units
/// from nowhere — no supplier, no purchase order, no delivery, no movement record. That job also
/// concealed D1: an oversell drove stock low, the job topped it up, and the discrepancy vanished
/// before anyone could see it. Overselling was undetectable from the data.
/// </para>
///
/// <para>
/// Note what this job does <b>not</b> do: it does not correct anything. A discrepancy means a
/// defect — a code path that moved stock without recording a movement — and silently healing it
/// is exactly the mistake Onyx made. It reports, loudly, and a human decides.
/// </para>
///
/// <para>
/// A discrepancy found here should page someone. An empty result is the only acceptable result.
/// </para>
/// </summary>
internal sealed class StockIntegrityCheckJob(
    JobRunner<CatalogDbContext> runner,
    TimeProvider timeProvider,
    ILogger<StockIntegrityCheckJob> logger,
    ReconciliationOptions? options = null) : BackgroundService
{
    public const string JobName = "catalog.stock-integrity-check";

    private readonly ReconciliationOptions _options = options ?? new ReconciliationOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.StockIntegrityInterval, timeProvider);

        logger.LogInformation(
            "Stock integrity check started, running every {Interval}.",
            _options.StockIntegrityInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // JobRunner records the attempt and absorbs any exception, so a bad pass cannot
            // stop this loop or take the host down with it.
            await runner.RunAsync(JobName, CheckAsync, stoppingToken);
        }
    }

    private async Task<JobResult> CheckAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<CatalogDbContext>();

        // Computed in SQL — a GROUP BY against an index, not a table load. Onyx A19 materialised
        // whole tables and then filtered in memory.
        var discrepancies = await context.Products
            .AsNoTracking()
            .Select(product => new
            {
                product.Id,
                Sku = product.Sku.Value,
                Recorded = product.Stock.Quantity,
                Movements = context.StockMovements
                    .Where(movement => movement.ProductId == product.Id)
                    .Sum(movement => movement.Delta)
            })
            .Where(row => row.Recorded != row.Movements)
            // Ordered because it is capped. Without a sort key SQL Server may return any 100 of
            // the matching rows, so a given discrepancy can be starved pass after pass while the
            // same rows come back — a real data-corruption defect reported for a while and then
            // silently not. That is Onyx A27 (unordered pagination) with teeth, and it was in
            // this file until the EF warning was promoted to an exception.
            // SKU is unique and indexed, so the order is total and progress is guaranteed.
            .OrderBy(row => row.Sku)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var discrepancy in discrepancies)
        {
            logger.LogError(
                "STOCK INTEGRITY FAILURE for {Sku} ({ProductId}): recorded quantity {Recorded} "
                + "but movements sum to {MovementSum}, a difference of {Difference}. Some code "
                + "path changed stock without recording a movement.",
                discrepancy.Sku,
                discrepancy.Id,
                discrepancy.Recorded,
                discrepancy.Movements,
                discrepancy.Recorded - discrepancy.Movements);
        }

        // ItemsFailed is the discrepancy count, so the JobRuns table shows a non-zero failure
        // count on any pass that found one — queryable without reading logs.
        return new JobResult(ItemsProcessed: discrepancies.Count, ItemsFailed: discrepancies.Count);
    }
}
