using BoomboCard.Promotions.Data;
using BoomboCard.Promotions.Data.Repositories;
using BoomboCard.Promotions.Domain.PromotionAggregate;
using BoomboCard.Promotions.Domain.PromotionAggregate.Events;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Promotions.Application.Maintenance;

public sealed class PromotionsOptions
{
    /// <summary>How often expired campaigns are closed.</summary>
    public TimeSpan ExpirySweepInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a processed outbox message is kept.</summary>
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Closes campaigns whose validity window has ended, and sweeps processed outbox messages.
///
/// <para>
/// <b>Why a job rather than relying on the window check.</b> <c>Promotion.Evaluate</c> already
/// refuses an expired promotion, so customers cannot use one whether or not this runs — the
/// correctness of the customer-facing path does not depend on it. What the job gives is an
/// accurate <i>status</i>: without it, every finished campaign sits in the admin list as
/// <c>Active</c> forever, and "which campaigns are running?" has no answer you can read off the
/// screen.
/// </para>
///
/// <para>
/// That distinction is worth stating because it is the opposite of Onyx <b>D5</b>, where the
/// background job was load-bearing for correctness in the worst way: its stock reconciliation
/// added 50 units from nowhere every 15 minutes, which silently healed every oversell before
/// anyone could see one. Here the job only records a fact that is already true, and nothing it
/// does invents anything.
/// </para>
///
/// <para>
/// It also publishes <c>PromotionClosedIntegrationEvent</c> with
/// <c>PromotionCloseReason.Expired</c>, so campaign reporting can distinguish a campaign that
/// ran its course from one that was exhausted or withdrawn. Onyx shipped <c>SUMMER25</c> already
/// expired and had no mechanism that could have noticed (<b>M16</b>).
/// </para>
/// </summary>
internal sealed class PromotionExpiryJob(
    JobRunner<PromotionsDbContext> runner,
    PromotionsOptions options,
    TimeProvider timeProvider,
    ILogger<PromotionExpiryJob> logger) : BackgroundService
{
    public const string JobName = "promotions.expiry-sweep";

    /// <summary>
    /// Capped per pass, so one sweep cannot hold a transaction open over ten thousand rows.
    /// The next tick picks up the rest.
    /// </summary>
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer on a fixed cadence, not Task.Delay from process start. Onyx A20's
        // "nightly" job was await Task.Delay(TimeSpan.FromHours(24)) measured from startup, so
        // it drifted later every day and a restart reset the phase.
        using var timer = new PeriodicTimer(options.ExpirySweepInterval, timeProvider);

        logger.LogInformation(
            "Promotion expiry sweep started, running every {Interval}.",
            options.ExpirySweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, SweepAsync, stoppingToken);
        }
    }

    private async Task<JobResult> SweepAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<PromotionsDbContext>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork<PromotionsDbContext>>();
        var nowUtc = timeProvider.GetUtcNow();

        var expired = await context.Promotions
            .Where(promotion =>
                promotion.Status.Value != PromotionStatusValue.Closed
                && promotion.Window.EndsAtUtc <= nowUtc)
            // Ordered because it is capped — an unordered Take would return an arbitrary 100 and
            // could starve the same campaigns pass after pass. That bug was live in Catalog's own
            // jobs (BUGS-AND-LESSONS §8), and the EF warning for it is now a thrown exception,
            // so omitting this would fail rather than merely be wrong.
            .OrderBy(promotion => promotion.Window.EndsAtUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return JobResult.Nothing;
        }

        foreach (var promotion in expired)
        {
            // Close raises the domain event, whose translator enqueues the outbox message. All of
            // it commits in the one SaveChanges below.
            promotion.Close(PromotionCloseReason.Expired, nowUtc);

            logger.LogInformation(
                "Promotion {Code} closed: its window ended at {EndsAt} with {Count} redemption(s).",
                promotion.Code.Value,
                promotion.Window.EndsAtUtc,
                promotion.RedemptionCount);
        }

        // SaveAndDispatchAsync, not SaveChangesAsync: a job is not dispatched through the MediatR
        // pipeline, so nothing has opened a transaction and nothing would drain the events Close
        // raised — which is precisely how the outbox messages get written. Calling the wrong one
        // now throws rather than silently dropping them; see IUnitOfWork.SaveChangesAsync.
        await unitOfWork.SaveAndDispatchAsync(cancellationToken);

        var messagesBefore = nowUtc - options.OutboxRetention;

        var messagesDeleted = await context.OutboxMessages
            .Where(message => message.ProcessedAtUtc != null
                && message.ProcessedAtUtc < messagesBefore)
            .ExecuteDeleteAsync(cancellationToken);

        return new JobResult(expired.Count + messagesDeleted);
    }
}
