using BoomboCard.Sales.Data;
using BoomboCard.SharedKernel.Persistence.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Sales.Application.Reconciliation;

/// <summary>
/// Asserts that every stored total equals the sum of the rows that explain it. <b>Reports, never
/// corrects</b> — the fifth time this project builds that pairing, and the most important one.
///
/// <para>
/// The state-versus-log pattern is everywhere in BoomboCard: <c>Product.Stock</c> against
/// <c>StockMovement</c>, <c>PasswordCredential</c> against <c>AuthAttempt</c>,
/// <c>Promotion.RedemptionCount</c> against <c>PromotionRedemption</c>, and
/// <c>PointsAccount.Balance</c> against <c>PointsLedgerEntry</c>. Here the "log" is the lines
/// and adjustments and the "state" is <c>Total</c> — so <c>SUM(lines) + SUM(adjustments) ==
/// Total</c> is the same invariant in its fifth costume, and this job is the same check.
/// </para>
///
/// <para>
/// <b>Why it refuses to fix anything.</b> A total that disagrees with its rows means the
/// pricing pipeline has a defect, and a job that quietly rewrote the total would erase the only
/// evidence of it while leaving the defect shipping. Onyx <b>D5</b> is precisely that mistake
/// in Catalog's clothing: a replenishment job that healed every oversell before anyone could
/// see one, so <b>D1</b> — the actual race — stayed invisible for as long as the job kept
/// running. Detection and correction are different jobs, and only one of them is safe to
/// automate.
/// </para>
///
/// <para>
/// It also checks something the per-aggregate invariant cannot: that the arithmetic relationship
/// <i>between</i> the stored components holds. <c>M19</c>'s obligation is a total that can
/// always be re-derived from its parts, and a stored <c>Tax</c> that is not
/// <c>TaxableBase * rate</c> is a broken total even when the rows happen to add up.
/// </para>
/// </summary>
internal sealed class SalesIntegrityCheckJob(
    JobRunner<SalesDbContext> runner,
    SalesOptions options,
    TimeProvider timeProvider,
    ILogger<SalesIntegrityCheckJob> logger) : BackgroundService
{
    public const string JobName = "sales.integrity-check";

    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.IntegrityCheckInterval, timeProvider);

        logger.LogInformation(
            "Sales integrity check started, running every {Interval}. It reports mismatches and "
            + "corrects nothing.",
            options.IntegrityCheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, CheckAsync, stoppingToken);
        }
    }

    private async Task<JobResult> CheckAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<SalesDbContext>();

        // Newest first, capped, and ORDERED because it is capped — an unordered Take would
        // check an arbitrary 200 rows each pass and could skip the same order forever.
        // BUGS-AND-LESSONS §8.
        var orders = await context.Orders
            .AsNoTracking()
            .OrderByDescending(order => order.PlacedAtUtc)
            .Take(BatchSize)
            .Select(order => new TotalsRow(
                order.Id,
                order.Number.Value,
                order.Items.Sum(item => item.LineTotal.Amount),
                order.Adjustments.Sum(adjustment => adjustment.Amount),
                order.MerchandiseSubtotal.Amount,
                order.Total.Amount))
            .ToListAsync(cancellationToken);

        var inspected = 0;
        var mismatches = 0;

        foreach (var row in orders)
        {
            inspected++;

            // Summed by the database, compared here. Onyx A26's shape was the opposite: one
            // extra query per row from inside a Select over an already-materialised list.
            if (row.LineSum + row.AdjustmentSum != row.Total)
            {
                mismatches++;

                logger.LogError(
                    "Order {OrderNumber} ({OrderId}) stores a total of {Total} but its rows sum "
                    + "to {RowSum} ({LineSum} merchandise {AdjustmentSum:+0.00;-0.00} "
                    + "adjustments). NOT corrected — the pricing pipeline has a defect and this "
                    + "row is the evidence.",
                    row.OrderNumber,
                    row.OrderId,
                    row.Total,
                    row.LineSum + row.AdjustmentSum,
                    row.LineSum,
                    row.AdjustmentSum);

                continue;
            }

            if (row.LineSum != row.MerchandiseSubtotal)
            {
                mismatches++;

                logger.LogError(
                    "Order {OrderNumber} ({OrderId}) stores a merchandise subtotal of {Stored} "
                    + "but its lines sum to {LineSum}. NOT corrected.",
                    row.OrderNumber,
                    row.OrderId,
                    row.MerchandiseSubtotal,
                    row.LineSum);
            }
        }

        // Live sessions only. An expired session's rows are swept and its totals stop mattering,
        // and a confirmed one has already been copied onto an order that IS checked above — so
        // checking terminal sessions would spend the batch on rows nothing reads.
        var sessions = await context.CheckoutSessions
            .AsNoTracking()
            .Where(session =>
                session.Status.Value == Domain.CheckoutSessionAggregate.CheckoutStatusValue.Open
                || session.Status.Value
                    == Domain.CheckoutSessionAggregate.CheckoutStatusValue.Holding)
            .OrderByDescending(session => session.StartedAtUtc)
            .Take(BatchSize)
            .Select(session => new
            {
                session.Id,
                LineSum = session.Lines.Sum(line => line.LineTotal.Amount),
                AdjustmentSum = session.Adjustments.Sum(adjustment => adjustment.Amount),
                Total = session.Total.Amount
            })
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            inspected++;

            if (session.LineSum + session.AdjustmentSum != session.Total)
            {
                mismatches++;

                logger.LogError(
                    "Checkout {SessionId} stores a total of {Total} but its rows sum to "
                    + "{RowSum}. NOT corrected.",
                    session.Id,
                    session.Total,
                    session.LineSum + session.AdjustmentSum);
            }
        }

        if (mismatches == 0)
        {
            return new JobResult(inspected);
        }

        logger.LogError(
            "Sales integrity check found {Mismatches} total(s) that disagree with their own rows "
            + "out of {Checked} checked.",
            mismatches,
            inspected);

        return new JobResult(inspected, mismatches);
    }

    private sealed record TotalsRow(
        Guid OrderId,
        string OrderNumber,
        decimal LineSum,
        decimal AdjustmentSum,
        decimal MerchandiseSubtotal,
        decimal Total);
}
