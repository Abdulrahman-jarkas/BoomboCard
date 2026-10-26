using BoomboCard.Loyalty.Data;
using BoomboCard.Loyalty.Data.Repositories;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Loyalty.Application.Maintenance;

public sealed class LoyaltyOptions
{
    /// <summary>How often lapsed points are written off.</summary>
    public TimeSpan ExpirySweepInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How often <c>SUM(Delta) == Balance</c> is asserted across accounts.</summary>
    public TimeSpan IntegrityCheckInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a processed outbox message is kept.</summary>
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Writes off points that have lapsed past their lifetime.
///
/// <para>
/// The arithmetic is <c>PointsAccount.ExpirableBefore</c>, and it needs no per-batch tracking:
/// because points are consumed oldest-first, whatever is left in an account is always the most
/// recently credited, so the amount still alive from before a cutoff is
/// <c>max(0, creditedBeforeCutoff - consumedTotal)</c>. Two indexed sums per account, no lot
/// entities, nothing mutable on a ledger row.
/// </para>
///
/// <para>
/// Unlike the customer-facing paths, this job <b>is</b> load-bearing: nothing else writes off
/// lapsed points, so if it never runs the liability never ends. That is the opposite of
/// Promotions' expiry sweep, which only records a status that <c>Evaluate</c> already enforces.
/// It is also the opposite of Onyx <b>D5</b> in the direction that matters — that job invented
/// stock from nowhere every 15 minutes and silently healed every oversell; this one only ever
/// removes value the scheme already said had a deadline, and it writes a ledger row saying so.
/// </para>
/// </summary>
internal sealed class PointsExpiryJob(
    JobRunner<LoyaltyDbContext> runner,
    LoyaltyOptions options,
    TimeProvider timeProvider,
    ILogger<PointsExpiryJob> logger) : BackgroundService
{
    public const string JobName = "loyalty.points-expiry";

    /// <summary>Capped per pass; the next tick takes the rest.</summary>
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ExpirySweepInterval, timeProvider);

        logger.LogInformation(
            "Points expiry started, running every {Interval}. Points live for {Lifetime}.",
            options.ExpirySweepInterval,
            EarnRule.PointsLifetime);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, SweepAsync, stoppingToken);
        }
    }

    private async Task<JobResult> SweepAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var points = services.GetRequiredService<IPointsRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork<LoyaltyDbContext>>();
        var context = services.GetRequiredService<LoyaltyDbContext>();

        var nowUtc = timeProvider.GetUtcNow();
        var cutoff = nowUtc - EarnRule.PointsLifetime;

        var candidates = await points.GetExpiryCandidatesAsync(cutoff, BatchSize, cancellationToken);

        var expiredAccounts = 0;
        var expiredPoints = 0;

        foreach (var accountId in candidates)
        {
            var account = await points.GetByIdAsync(accountId, cancellationToken);

            if (account is null)
            {
                continue;
            }

            var (credited, consumed) = await points.GetExpiryFiguresAsync(
                accountId, cutoff, cancellationToken);

            var lapsed = PointsAccount.ExpirableBefore(credited, consumed);

            if (lapsed <= 0)
            {
                continue;
            }

            // The reference carries the cutoff, which makes the write idempotent per sweep
            // window: a second pass on the same day finds the entry already present under
            // UX_PointsLedger_Idempotency and the Expire below is skipped by the check.
            var reference = $"expiry:{cutoff:yyyy-MM-dd}";

            if (await points.EntryExistsAsync(
                    accountId, reference, Domain.Ledger.PointsReason.Expired, cancellationToken))
            {
                continue;
            }

            var entry = account.Expire(lapsed, reference, nowUtc);

            if (entry.IsError)
            {
                logger.LogError(
                    "Could not expire {Points} points on account {AccountId}: {Error}",
                    lapsed,
                    accountId,
                    entry.FirstError.Description);

                continue;
            }

            points.AddEntry(entry.Value);
            expiredAccounts++;
            expiredPoints += lapsed;

            logger.LogInformation(
                "Expired {Points} points on account {AccountId}, earned before {Cutoff}.",
                lapsed,
                accountId,
                cutoff);
        }

        if (expiredAccounts > 0)
        {
            // SaveAndDispatchAsync: a job is not in the MediatR pipeline, so nothing has opened
            // a transaction and nothing would drain the PointsMovedEvent the expiry raised —
            // which is how PointsExpiredIntegrationEvent reaches the outbox. See bug 10.
            await unitOfWork.SaveAndDispatchAsync(cancellationToken);
        }

        var messagesBefore = nowUtc - options.OutboxRetention;

        var messagesDeleted = await context.OutboxMessages
            .Where(message => message.ProcessedAtUtc != null
                && message.ProcessedAtUtc < messagesBefore)
            .ExecuteDeleteAsync(cancellationToken);

        return new JobResult(expiredAccounts + messagesDeleted);
    }
}

/// <summary>
/// Asserts <c>SUM(Delta) == Balance</c> for every account, and <b>reports rather than
/// corrects</b>.
///
/// <para>
/// This is the job that makes the balance column trustworthy. The ledger is the authoritative
/// record and the balance is an enforceable projection of it; without something checking that
/// they agree, the projection is just a counter — which is what Onyx had.
/// </para>
///
/// <para>
/// <b>It never fixes anything</b>, and that is the entire point. Onyx <b>D5</b>'s stock
/// reconciliation job silently healed every discrepancy it found by adding 50 units from
/// nowhere, so the oversells it was papering over were invisible to everyone. A mismatch here
/// is a data-corruption defect: it goes into <c>ItemsFailed</c> on the <c>JobRun</c> row, where
/// it is queryable, and into the log at <c>Error</c>. Catalog's <c>StockIntegrityCheckJob</c> is
/// the same job over stock, written for the same reason.
/// </para>
/// </summary>
internal sealed class PointsIntegrityCheckJob(
    JobRunner<LoyaltyDbContext> runner,
    LoyaltyOptions options,
    TimeProvider timeProvider,
    ILogger<PointsIntegrityCheckJob> logger) : BackgroundService
{
    public const string JobName = "loyalty.points-integrity-check";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.IntegrityCheckInterval, timeProvider);

        logger.LogInformation(
            "Points integrity check started, running every {Interval}.",
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
        var context = services.GetRequiredService<LoyaltyDbContext>();

        // Computed in SQL — a correlated sum against IX_PointsLedger_Statement, not a table
        // load. Onyx A19 materialised whole tables and then filtered in memory.
        var discrepancies = await context.Accounts
            .AsNoTracking()
            .Select(account => new
            {
                account.Id,
                account.CustomerId,
                Recorded = account.Balance,
                Ledger = context.Ledger
                    .Where(entry => entry.AccountId == account.Id)
                    .Sum(entry => entry.Delta)
            })
            .Where(row => row.Recorded != row.Ledger)
            // Ordered because it is capped: an unordered Take can return a different arbitrary
            // 100 every pass, so a given discrepancy is not guaranteed to be the one reported.
            // That bug was live in Catalog's own jobs (BUGS-AND-LESSONS §8).
            .OrderBy(row => row.Id)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var discrepancy in discrepancies)
        {
            logger.LogError(
                "POINTS INTEGRITY FAILURE on account {AccountId} (customer {CustomerId}): "
                + "recorded balance {Recorded} but the ledger sums to {Ledger}. "
                + "Reported, NOT corrected — see PointsIntegrityCheckJob.",
                discrepancy.Id,
                discrepancy.CustomerId,
                discrepancy.Recorded,
                discrepancy.Ledger);
        }

        // ItemsFailed is non-zero on any pass that finds one, which makes it queryable through
        // the JobRuns endpoint. Should always be zero; anything else is worth paging someone.
        return new JobResult(ItemsProcessed: 0, ItemsFailed: discrepancies.Count);
    }
}
