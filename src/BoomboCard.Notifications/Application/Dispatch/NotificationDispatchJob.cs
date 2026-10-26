using BoomboCard.Notifications.Application.Notifications.Commands;
using BoomboCard.Notifications.Data;
using BoomboCard.Notifications.Data.Repositories;
using BoomboCard.Notifications.Domain.NotificationAggregate;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Notifications.Application.Dispatch;

/// <summary>
/// Sends what is due. <b>The thing Onyx did not have.</b>
///
/// <para>
/// <b>A8</b> in full: <i>"No customer has ever received any communication from this system, and
/// nothing records that. There is no retry, no dead-letter, no delivery record."</i> Its entire
/// delivery mechanism was <c>_ = Task.Run(() =&gt; SendConfirmationEmail(...))</c> against a
/// method whose body threw <c>NotImplementedException</c> — fired inline, on the request thread's
/// behalf, discarded, with nothing scheduled and nothing recorded.
/// </para>
///
/// <para>
/// <b>The roadmap called this "the outbox shape, because the problem is the outbox problem", and
/// it is right about the mechanics.</b> Claim, send, record, back off, dead-letter — the same five
/// steps <c>OutboxDispatcher</c> performs, for the same reason. What differs is what is being
/// moved: the outbox carries this system's own events between its own modules, so its rows are
/// transient and its payload is opaque. A notification is a business fact with a recipient, a
/// legible body, an audit trail, a retention rule and an administrative resend.
/// </para>
///
/// <para>
/// So it borrows the shape rather than the table, and the differences are the interesting part:
/// </para>
///
/// <list type="bullet">
/// <item><b>A rejection is terminal and costs no attempt.</b> The outbox has no equivalent — an
/// internal consumer cannot tell you your address is wrong. A bad recipient is a data problem, and
/// retrying it five times wastes the budget a genuinely transient failure needs.</item>
/// <item><b>The claim is committed before the send.</b> The outbox can be more relaxed, because a
/// redelivered internal event hits an inbox guard. A duplicate email has no such guard — the
/// recipient is the guard, and they are not amused.</item>
/// <item><b>A dead letter is published.</b> An outbox dead-letter is an operational row; a
/// notification dead-letter means a customer was not told something, which for a password reset
/// means they are locked out and do not know why.</item>
/// </list>
///
/// <para>
/// Every pass goes through <c>JobRunner</c>, so a run row exists before the work starts and the
/// outcome is recorded with real counts afterwards. Onyx <b>A22</b> logged "pass complete for
/// {Count} products" <i>before</i> any work had run, because the work was <c>async void</c> — the
/// message was identical whether it succeeded, failed, or never happened.
/// </para>
/// </summary>
internal sealed class NotificationDispatchJob(
    JobRunner<NotificationsDbContext> runner,
    NotificationsOptions options,
    TimeProvider timeProvider,
    ILogger<NotificationDispatchJob> logger) : BackgroundService
{
    public const string JobName = "notifications.dispatch";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, timeProvider);

        logger.LogInformation(
            "Notification dispatch started, polling every {Interval} in batches of {BatchSize}. "
            + "Up to {MaxAttempts} attempts per notification before dead-lettering.",
            options.PollInterval,
            options.BatchSize,
            NotificationRequest.MaxAttempts);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, DispatchAsync, stoppingToken);
        }
    }

    private async Task<JobResult> DispatchAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var notifications = services.GetRequiredService<INotificationRepository>();
        var dispatcher = services.GetRequiredService<ICommandDispatcher>();

        var nowUtc = timeProvider.GetUtcNow();

        var due = await notifications.GetDueIdsAsync(nowUtc, options.BatchSize, cancellationToken);

        var sent = 0;
        var failed = 0;

        foreach (var notificationId in due)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Per-notification error handling, deliberately.
            //
            // Throwing would abandon the rest of the batch, and the batch is ordered oldest-first
            // — so one permanently broken notification would block every message behind it
            // forever. That is exactly RiverBooks' outbox poller, which never marks a failure, so
            // one bad message stops the queue.
            try
            {
                var result = await dispatcher.SendAsync(
                    new DispatchNotificationCommand(notificationId), cancellationToken);

                if (result.IsError)
                {
                    failed++;
                    continue;
                }

                if (result.Value.Sent)
                {
                    sent++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(
                    exception,
                    "Dispatching notification {NotificationId} threw. The rest of the batch "
                    + "continues; the stale-claim sweep will requeue this one.",
                    notificationId);

                failed++;
            }
        }

        return new JobResult(sent, failed);
    }
}

/// <summary>
/// Two sweeps that keep the queue honest, plus the outbox trim.
///
/// <para>
/// One job rather than three, because all three are cheap table scans on a timer and three
/// hosted services would be three sets of logs saying nothing.
/// </para>
/// </summary>
internal sealed class NotificationMaintenanceJob(
    JobRunner<NotificationsDbContext> runner,
    NotificationsOptions options,
    TimeProvider timeProvider,
    ILogger<NotificationMaintenanceJob> logger) : BackgroundService
{
    public const string JobName = "notifications.maintenance";

    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.MaintenanceInterval, timeProvider);

        logger.LogInformation(
            "Notification maintenance started, running every {Interval}. Claims go stale after "
            + "{Stale}; rendered secrets are discarded {Retention} after delivery.",
            options.MaintenanceInterval,
            options.StaleClaimThreshold,
            options.SecretRetention);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, SweepAsync, stoppingToken);
        }
    }

    private async Task<JobResult> SweepAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var notifications = services.GetRequiredService<INotificationRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork<NotificationsDbContext>>();

        var nowUtc = timeProvider.GetUtcNow();
        var requeued = 0;
        var forgotten = 0;

        // ---- sweep 1: claims nobody completed.
        //
        // A process killed between claiming and sending leaves a row in Sending. Without this it
        // stays there forever — which is Onyx's "silently and forever" reached by a new road, and
        // the reason JobRunner writes a run row before the work starts is the same instinct: a
        // thing stuck mid-flight should be visible, not absent.
        var stale = await notifications.GetStaleClaimIdsAsync(
            nowUtc - options.StaleClaimThreshold, BatchSize, cancellationToken);

        foreach (var notificationId in stale)
        {
            var notification = await notifications.GetAsync(notificationId, cancellationToken);

            if (notification is null)
            {
                continue;
            }

            var released = notification.ReleaseStaleClaim(nowUtc);

            if (released.IsError)
            {
                continue;
            }

            requeued++;

            logger.LogWarning(
                "Notification {NotificationId} ({Kind}) was claimed at {ClaimedAt} and never "
                + "completed. Requeued — a duplicate send is possible, which is the cheaper "
                + "failure.",
                notification.Id,
                notification.Kind,
                nowUtc - options.StaleClaimThreshold);
        }

        // ---- sweep 2: rendered secrets past their retention.
        //
        // A verification or reset body is a live credential. The token's own expiry is the primary
        // control; this is defence in depth, because the notifications table is exactly the thing
        // that gets copied into a reporting warehouse. Onyx S14 stored reset tokens in plaintext
        // with no expiry at all.
        var secrets = await notifications.GetStaleSecretIdsAsync(
            nowUtc - options.SecretRetention, BatchSize, cancellationToken);

        foreach (var notificationId in secrets)
        {
            var notification = await notifications.GetAsync(notificationId, cancellationToken);

            if (notification?.ForgetBody() is true)
            {
                forgotten++;
            }
        }

        if (requeued > 0 || forgotten > 0)
        {
            // SaveAndDispatchAsync, not SaveChangesAsync: a job is not in the request pipeline, so
            // nothing has opened a transaction and nothing would drain the domain events.
            // BUGS-AND-LESSONS §10 — and SaveChangesAsync now throws rather than discarding them.
            await unitOfWork.SaveAndDispatchAsync(cancellationToken);

            if (forgotten > 0)
            {
                logger.LogInformation(
                    "Discarded the rendered body of {Count} delivered notification(s) that "
                    + "contained a token.",
                    forgotten);
            }
        }

        return new JobResult(requeued + forgotten);
    }
}

/// <summary>
/// Reports the delivery figures. <b>Reports, never corrects.</b>
///
/// <para>
/// The sixth integrity check in this codebase, and the one whose mere existence answers the half
/// of <b>A8</b> that is easy to overlook. The first half of that finding is "no customer has ever
/// received any communication from this system" — a missing implementation, a day's work. The
/// second is <i>"and nothing records that"</i> — and that is why it survived long enough to be
/// written in an audit.
/// </para>
///
/// <para>
/// A pass that finds a dead letter writes a non-zero <c>ItemsFailed</c> to <c>JobRuns</c>, which
/// makes it queryable. Onyx <b>A7</b>'s worker-status panel read <c>static</c> fields written in a
/// <i>different process</i> and returned null from the day it shipped.
/// </para>
///
/// <para>
/// It also asserts the one invariant the retention job is responsible for: no finished
/// notification should still be holding a rendered secret. Reporting rather than clearing is
/// deliberate — silently clearing would hide a retention job that had stopped running, which is
/// Onyx <b>D5</b>'s shape.
/// </para>
/// </summary>
internal sealed class NotificationsIntegrityCheckJob(
    JobRunner<NotificationsDbContext> runner,
    NotificationsOptions options,
    TimeProvider timeProvider,
    ILogger<NotificationsIntegrityCheckJob> logger) : BackgroundService
{
    public const string JobName = "notifications.integrity-check";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.IntegrityCheckInterval, timeProvider);

        logger.LogInformation(
            "Notification integrity check started, running every {Interval}. It reports and "
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
        var context = services.GetRequiredService<NotificationsDbContext>();
        var nowUtc = timeProvider.GetUtcNow();

        var counts = await context.Notifications
            .AsNoTracking()
            .GroupBy(notification => notification.Status.Value)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var total = counts.Sum(row => row.Count);
        var deadLettered = counts.FirstOrDefault(row => row.Status == NotificationStatusValue.DeadLettered)?.Count ?? 0;
        var rejected = counts.FirstOrDefault(row => row.Status == NotificationStatusValue.Rejected)?.Count ?? 0;
        var queued = counts.FirstOrDefault(row => row.Status == NotificationStatusValue.Queued)?.Count ?? 0;
        var sending = counts.FirstOrDefault(row => row.Status == NotificationStatusValue.Sending)?.Count ?? 0;
        var sent = counts.FirstOrDefault(row => row.Status == NotificationStatusValue.Sent)?.Count ?? 0;

        var problems = 0;

        // Notifications that reached a terminal state and left no attempt row. Structurally
        // unreachable — RecordDeliveryOutcomeCommand writes the row before the transition, and
        // unconditionally — so if it ever happens, the audit trail has a hole in it.
        var undocumented = await context.Notifications
            .AsNoTracking()
            .Where(notification =>
                (notification.Status.Value == NotificationStatusValue.Sent
                    || notification.Status.Value == NotificationStatusValue.Rejected
                    || notification.Status.Value == NotificationStatusValue.DeadLettered)
                && !notification.Attempts.Any())
            .CountAsync(cancellationToken);

        if (undocumented > 0)
        {
            problems += undocumented;

            logger.LogError(
                "{Count} notification(s) are in a terminal delivery state with NO attempt rows. "
                + "The delivery record has a hole in it, which is the half of Onyx A8 this module "
                + "exists to close. NOT corrected.",
                undocumented);
        }

        // Rendered secrets past their retention. The retention job's job; reporting it here is
        // what catches that job having stopped.
        var staleSecrets = await context.Notifications
            .AsNoTracking()
            .Where(notification =>
                notification.ContainsSecret
                && notification.Body != string.Empty
                && notification.FinishedAtUtc != null
                && notification.FinishedAtUtc <= nowUtc - options.SecretRetention)
            .CountAsync(cancellationToken);

        if (staleSecrets > 0)
        {
            problems += staleSecrets;

            logger.LogError(
                "{Count} finished notification(s) still hold a rendered token past the "
                + "{Retention} retention window. notifications.maintenance should have discarded "
                + "them — check it is running. NOT corrected here, deliberately: clearing them "
                + "silently would hide the job having stopped.",
                staleSecrets,
                options.SecretRetention);
        }

        if (deadLettered > 0)
        {
            problems += deadLettered;

            logger.LogError(
                "{DeadLettered} notification(s) have been dead-lettered and will never be "
                + "delivered. Each one is a customer who was not told something.",
                deadLettered);
        }

        logger.LogInformation(
            "Notifications: {Sent} sent, {Queued} queued, {Sending} sending, {Rejected} "
            + "rejected, {DeadLettered} dead-lettered, out of {Total}.",
            sent,
            queued,
            sending,
            rejected,
            deadLettered,
            total);

        return new JobResult(total, problems);
    }
}
