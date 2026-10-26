using System.Diagnostics;
using System.Text.Json;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Observability;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.SharedKernel.Persistence.Outbox;

public sealed class OutboxOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; init; } = 50;

    /// <summary>Attempts before a message is dead-lettered and stops being retried.</summary>
    public int MaxAttempts { get; init; } = 8;
}

/// <summary>
/// Publishes integration events that were committed to the outbox, then marks them done.
///
/// This is the half of the outbox pattern that makes the other half worth anything: without a
/// dispatcher, messages are written atomically and never delivered.
///
/// Four properties it deliberately has, each one fixing something in the systems I studied:
///
///  1. <b>Per-message error handling.</b> A failing message is marked and skipped; the rest of
///     the batch still goes out. RiverBooks' poller takes the first unprocessed row and never
///     marks it on failure, so one permanently broken message is retried forever and blocks
///     every message behind it — head-of-line blocking with no escape.
///
///  2. <b>Per-iteration error boundary.</b> Any exception escaping a pass is logged and the loop
///     continues. Onyx A18/A21: an unhandled DivideByZeroException in one background job
///     terminated the whole worker process under the default StopHost behaviour, taking the
///     unrelated stock job down with it, with nothing to restart either.
///
///  3. <b>A real timer.</b> PeriodicTimer ticks on a fixed cadence. Onyx A20 used
///     <c>await Task.Delay(TimeSpan.FromHours(24))</c> measured from process start, so its
///     "nightly" job drifted later every day and a restart reset the phase.
///
///  4. <b>Outcomes logged after the work.</b> Onyx A22 printed
///     "pass complete for {Count} products" before any of the work had run, because the work
///     was <c>async void</c>. The message was identical whether it succeeded, failed, or never
///     happened.
///
/// <para>
/// <b>And one more, which is a tracing property rather than a correctness one.</b> Each message
/// is published inside a span whose parent is the trace context stored on the row, so the
/// publish and everything it causes land in the <i>same</i> trace as the command that queued it
/// — even though this runs on a later timer tick, on another thread, in another DI scope, with
/// no ambient activity. The alternative is the default, and the default is a separate
/// single-span trace per dispatch, which answers no question anybody actually has. See
/// <see cref="TraceContext"/>.
/// </para>
/// </summary>
public sealed class OutboxDispatcher<TContext>(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher<TContext>> logger,
    OutboxOptions? options = null) : BackgroundService
    where TContext : DbContext
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly string ModuleName = BoomboCardTelemetry.ModuleNameOf(typeof(TContext));

    private readonly OutboxOptions _options = options ?? new OutboxOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval, timeProvider);

        logger.LogInformation(
            "Outbox dispatcher started for {Context}, polling every {Interval}.",
            typeof(TContext).Name,
            _options.PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var (published, failed) = await ProcessBatchAsync(stoppingToken);

                // Only log when something happened, and only after it happened.
                if (published > 0 || failed > 0)
                {
                    logger.LogInformation(
                        "Outbox pass complete for {Context}: {Published} published, {Failed} failed.",
                        typeof(TContext).Name,
                        published,
                        failed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // One bad pass must never stop the loop, let alone the process.
                logger.LogError(
                    exception,
                    "Outbox pass failed for {Context}. Continuing on the next tick.",
                    typeof(TContext).Name);
            }
        }

        logger.LogInformation("Outbox dispatcher stopping for {Context}.", typeof(TContext).Name);
    }

    private async Task<(int Published, int Failed)> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();

        var nowUtc = timeProvider.GetUtcNow();

        // Pending, backoff elapsed, oldest first. Served by IX_OutboxMessages_Dispatch.
        var batch = await context.Set<OutboxMessage>()
            .Where(message => message.Status == OutboxStatus.Pending
                && message.NextAttemptAtUtc != null
                && message.NextAttemptAtUtc <= nowUtc)
            .OrderBy(message => message.OccurredAtUtc)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            // No span for an empty pass. At a two-second poll across seven modules that is
            // roughly 300,000 spans a day all saying nothing happened, which is how a trace
            // store becomes too expensive to keep and too noisy to read. Same judgement as the
            // log line above, for the same reason.
            return (0, 0);
        }

        // The poller's own span, and deliberately a ROOT: its parent is a clock, not a request.
        // Hanging it off one of the messages it happens to be carrying would claim that one
        // checkout caused this pass, which is false, and would put the batch size and failure
        // count of unrelated work inside that checkout's trace.
        //
        // The flows it serves are reachable through links instead, which is what links are for:
        // a relationship between traces that is not causation.
        using var pass = BoomboCardTelemetry.Source.StartActivity(
            $"outbox pass {ModuleName}",
            ActivityKind.Internal,
            parentContext: default,
            links: LinksTo(batch));

        pass?.SetTag(BoomboCardTelemetry.ModuleTag, ModuleName);
        pass?.SetTag(BoomboCardTelemetry.OutboxBatchSizeTag, batch.Count);
        pass?.SetTag(BoomboCardTelemetry.MessagingSystemTag, BoomboCardTelemetry.MessagingSystem);
        pass?.SetTag(BoomboCardTelemetry.MessagingOperationTag, "receive");

        var published = 0;
        var failed = 0;

        foreach (var message in batch)
        {
            if (await PublishAsync(publisher, message, nowUtc, cancellationToken))
            {
                published++;
            }
            else
            {
                failed++;
            }
        }

        pass?.SetTag(BoomboCardTelemetry.OutboxPublishedTag, published);
        pass?.SetTag(BoomboCardTelemetry.OutboxFailedTag, failed);

        if (failed > 0)
        {
            // The pass itself did not throw — per-message failures are handled and the batch
            // completes, which is the point of (1) above. But a pass that failed every message
            // must not look identical in a dashboard to one that delivered them all.
            pass?.SetStatus(ActivityStatusCode.Error, $"{failed} of {batch.Count} messages failed.");
        }

        await context.SaveChangesAsync(cancellationToken);

        return (published, failed);
    }

    /// <summary>
    /// Publishes one message inside a span rooted in the trace that produced it. Returns whether
    /// it went out; a failure is recorded on the message and on the span and never rethrown, so
    /// the next message in the batch still gets its turn.
    /// </summary>
    private async Task<bool> PublishAsync(
        IPublisher publisher,
        OutboxMessage message,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        // default when the row carries no usable context — a message enqueued with tracing off,
        // or before this column existed. Worth knowing what that does: ActivitySource treats a
        // default ActivityContext as "no parent specified" rather than as "no parent", and falls
        // back to Activity.Current, which here is the pass span. So an untraced message is
        // grouped under the poll that delivered it instead of becoming an orphan root, which is
        // the more useful of the two outcomes and the reason this is left as it is rather than
        // forced to a root.
        var parent = TraceContext.Restore(message.TraceParent, message.TraceState);

        // The span name carries the event's own type rather than the module's, because this is
        // the row a reader scans a waterfall for. It is derived from the stored type name as a
        // string, without resolving the type — so the one message guaranteed to fail below, the
        // one whose contract was renamed, still gets a usefully named span.
        using var activity = BoomboCardTelemetry.Source.StartActivity(
            $"outbox publish {ShortTypeName(message.Type)}",

            // Consumer: the receiving end of an asynchronous message. This is what tells a
            // backend to draw the gap between the enqueue and here as a handoff rather than as a
            // suspiciously slow function call.
            ActivityKind.Consumer,
            parent);

        activity?.SetTag(BoomboCardTelemetry.ModuleTag, ModuleName);
        activity?.SetTag(BoomboCardTelemetry.MessageTypeTag, ShortTypeName(message.Type));
        activity?.SetTag(BoomboCardTelemetry.EventKindTag, "integration");
        activity?.SetTag(BoomboCardTelemetry.OutboxAttemptTag, message.AttemptCount + 1);
        activity?.SetTag(BoomboCardTelemetry.MessagingSystemTag, BoomboCardTelemetry.MessagingSystem);
        activity?.SetTag(BoomboCardTelemetry.MessagingOperationTag, "process");
        activity?.SetTag(BoomboCardTelemetry.MessagingMessageIdTag, message.Id);
        activity?.SetTag(BoomboCardTelemetry.MessagingDestinationTag, ModuleName);

        try
        {
            var integrationEvent = Deserialise(message);

            // Every consumer of this contract runs inside this span, so their inbox claims,
            // their commands and the outbox rows they write in turn all inherit the originating
            // trace. That inheritance is the mechanism by which one checkout becomes one trace
            // spanning Sales, Payments, Loyalty and Notifications.
            await publisher.Publish(integrationEvent, cancellationToken);

            message.MarkPublished(nowUtc);
            activity?.SetTag(BoomboCardTelemetry.OutboxStatusTag, message.Status.ToString());

            return true;
        }
        catch (Exception exception)
        {
            // Marked and moved past. The next message in the batch still gets its turn.
            message.MarkFailed(exception.Message, nowUtc, _options.MaxAttempts);

            activity.RecordFailure(exception);
            activity?.SetTag(BoomboCardTelemetry.OutboxStatusTag, message.Status.ToString());
            activity?.SetTag(BoomboCardTelemetry.OutboxAttemptTag, message.AttemptCount);

            logger.LogError(
                exception,
                "Outbox message {MessageId} of type {MessageType} failed on attempt {Attempt}. Status is now {Status}.",
                message.Id,
                message.Type,
                message.AttemptCount,
                message.Status);

            return false;
        }
    }

    /// <summary>
    /// One link per distinct flow in the batch, so the pass span is a way into the traces it
    /// served without pretending to have caused them.
    ///
    /// <para>
    /// Distinct by trace rather than one per message: a batch is usually several messages from
    /// the same checkout, and fifty links to one trace is fifty rows of the same answer.
    /// Messages with no stored context contribute nothing, since an all-zero context is not a
    /// trace.
    /// </para>
    /// </summary>
    private static IEnumerable<ActivityLink>? LinksTo(IReadOnlyCollection<OutboxMessage> batch)
    {
        var links = batch
            .Select(message => TraceContext.Restore(message.TraceParent, message.TraceState))
            .Where(context => context != default)
            .DistinctBy(context => context.TraceId)
            .Select(context => new ActivityLink(context))
            .ToList();

        return links.Count == 0 ? null : links;
    }

    /// <summary>
    /// <c>BoomboCard.Sales.Contracts.X, BoomboCard.Sales.Contracts, Version=...</c> becomes
    /// <c>X</c>, by string, without resolving the type — so it also works for the message that
    /// is about to fail <see cref="Deserialise"/> because its type is gone.
    /// </summary>
    private static string ShortTypeName(string assemblyQualifiedName)
    {
        var fullName = assemblyQualifiedName.Split(',')[0];
        var lastDot = fullName.LastIndexOf('.');

        return lastDot >= 0 && lastDot < fullName.Length - 1 ? fullName[(lastDot + 1)..] : fullName;
    }

    private static IIntegrationEvent Deserialise(OutboxMessage message)
    {
        var type = Type.GetType(message.Type, throwOnError: false)
            ?? throw new InvalidOperationException(
                $"Cannot resolve outbox message type '{message.Type}'. The contract may have been "
                + "renamed or moved — a reason to dead-letter rather than retry forever.");

        return JsonSerializer.Deserialize(message.Payload, type, SerializerOptions) as IIntegrationEvent
            ?? throw new InvalidOperationException(
                $"Outbox message {message.Id} did not deserialise into an IIntegrationEvent.");
    }
}
