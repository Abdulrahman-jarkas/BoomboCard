using BoomboCard.Sales.Data;
using BoomboCard.Sales.Data.Repositories;
using BoomboCard.Sales.Domain.CheckoutSessionAggregate;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Sales.Application.Reconciliation;

/// <summary>
/// Expires checkout sessions that passed their deadline without resolving. <b>Recovery layer
/// 2</b>, and the layer the whole three-transaction flow actually leans on.
///
/// <para>
/// Layer 1 — inline compensation inside <c>ConfirmCheckoutCommand</c> — is best-effort by
/// construction: it is code running in a process that may be the thing that just died. This job
/// is the one that does not depend on the failing request surviving long enough to clean up
/// after itself, which is why it is a sweeper on a timer rather than a <c>finally</c> block.
/// </para>
///
/// <para>
/// <b>It expires the session; it does not release anything.</b> Expiring raises
/// <c>CheckoutExpiredEvent</c>, which the outbox turns into
/// <c>CheckoutAbandonedIntegrationEvent</c>, which <c>OnCheckoutAbandonedReleaseHolds</c>
/// consumes. The separation is what makes the release guaranteed: the outbox row commits in the
/// same transaction as the expiry, so there is no state in which a session is expired and its
/// stock is not scheduled to come back.
/// </para>
///
/// <para>
/// <b>This job is load-bearing.</b> Nothing else releases a session that simply stopped — a
/// customer closing the tab sends no request. Without it, Onyx's exposure returns: stock held
/// against a checkout that never resolves, forever, with ISSUES.md <b>D8</b> ("stock is deducted
/// before payment with no compensation") never closed. Every pass is recorded through
/// <c>JobRunner</c>, so "did this run and did it work" is answerable from <c>JobRuns</c> rather
/// than from log archaeology.
/// </para>
/// </summary>
internal sealed class CheckoutExpiryJob(
    JobRunner<SalesDbContext> runner,
    SalesOptions options,
    TimeProvider timeProvider,
    ILogger<CheckoutExpiryJob> logger) : BackgroundService
{
    public const string JobName = "sales.checkout-expiry";

    /// <summary>Capped per pass; the next tick takes the rest.</summary>
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ExpirySweepInterval, timeProvider);

        logger.LogInformation(
            "Checkout expiry started, running every {Interval}. Sessions live for {Lifetime}.",
            options.ExpirySweepInterval,
            CheckoutSession.Lifetime);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, SweepAsync, stoppingToken);
        }
    }

    private async Task<JobResult> SweepAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var sales = services.GetRequiredService<ISalesRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork<SalesDbContext>>();

        var nowUtc = timeProvider.GetUtcNow();

        var candidates = await sales.GetExpiredSessionIdsAsync(nowUtc, BatchSize, cancellationToken);

        var expired = 0;
        var failed = 0;

        foreach (var sessionId in candidates)
        {
            var session = await sales.GetSessionAsync(sessionId, cancellationToken);

            if (session is null)
            {
                continue;
            }

            var result = session.Expire(nowUtc);

            if (result.IsError)
            {
                // A session confirmed between the id query and this load. Not an error worth
                // alerting on — it is the race resolving the right way — but counted, because a
                // pass that reports zero failures when it skipped work would be lying.
                failed++;
                continue;
            }

            expired++;
        }

        if (expired > 0)
        {
            // SaveAndDispatchAsync, once, for the whole batch: the expiries and their outbox
            // rows commit together. A job is not in the request pipeline, so nothing else
            // would drain the domain events — BUGS-AND-LESSONS §10, and SaveChangesAsync now
            // throws here rather than silently discarding them.
            await unitOfWork.SaveAndDispatchAsync(cancellationToken);

            logger.LogInformation(
                "Expired {Count} checkout sessions. Their holds are released by the outbox.",
                expired);
        }

        return new JobResult(expired, failed);
    }
}
