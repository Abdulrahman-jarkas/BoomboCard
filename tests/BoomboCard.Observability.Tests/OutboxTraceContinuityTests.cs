using System.Diagnostics;
using BoomboCard.SharedKernel.Observability;
using BoomboCard.SharedKernel.Persistence.Outbox;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoomboCard.Observability.Tests;

/// <summary>
/// The feature, end to end: a trace that starts in a command is still the same trace when the
/// background dispatcher publishes the message that command queued, and when the consumer of
/// that message queues another one.
///
/// <para>
/// This is the suite that would fail if somebody removed the trace columns, stopped capturing in
/// <c>OutboxWriter</c>, or started the publish span without a parent — each of which leaves
/// every other test in the solution green and quietly turns one readable flow back into a
/// scatter of disconnected single-span traces.
/// </para>
/// </summary>
[Collection("tracing")]
public sealed class OutboxTraceContinuityTests(SqlServerFixture fixture)
    : IClassFixture<SqlServerFixture>, IAsyncLifetime
{
    /// <summary>
    /// A timestamp in the recent past, not a fixed literal.
    ///
    /// <para>
    /// <c>OutboxMessage.Create</c> sets <c>NextAttemptAtUtc</c> from this, and the dispatcher
    /// only picks up rows whose next attempt has elapsed according to <c>TimeProvider.System</c>
    /// — which it has to be here, because exercising a real asynchronous handoff needs a real
    /// <c>PeriodicTimer</c>. A fixed literal is therefore a time bomb rather than determinism: a
    /// date even hours in the future makes the batch query match nothing and every dispatcher
    /// test time out, which is precisely what the first run of this suite did.
    /// </para>
    ///
    /// <para>
    /// Nothing here asserts on the value itself, so there is no determinism to lose.
    /// </para>
    /// </summary>
    private static DateTimeOffset Elapsed => DateTimeOffset.UtcNow.AddMinutes(-5);

    private readonly ActivityRecorder _recorder = new();

    /// <summary>
    /// An empty outbox before every test in this class.
    ///
    /// <para>
    /// The class fixture is one database shared by every test here, and the dispatcher's query
    /// is "every pending message", not "the one this test just wrote". So a producing-side test
    /// that enqueues a row and never dispatches it leaves work for whichever dispatcher test
    /// runs next, which then legitimately publishes two messages and fails an assertion about
    /// one. That is a real property of the dispatcher rather than a quirk of the test — it is
    /// supposed to drain whatever it finds — so the fix belongs here, in the setup, rather than
    /// in assertions loosened to tolerate strangers.
    /// </para>
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var context = fixture.CreateContext();

        await context.OutboxMessages.ExecuteDeleteAsync();
        await context.InboxMessages.ExecuteDeleteAsync();
    }

    public Task DisposeAsync()
    {
        _recorder.Dispose();

        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------- the producing side

    [Fact]
    public async Task EnqueuingInsideACommandStoresThatCommandsTraceOnTheRow()
    {
        await using var context = fixture.CreateContext();
        var writer = new OutboxWriter<TracingDbContext>(context);

        var integrationEvent = new TestIntegrationEvent(Guid.NewGuid(), Elapsed);

        string expectedTraceId;
        string expectedSpanId;

        using (var command = BoomboCardTelemetry.Source.StartActivity("command Checkout"))
        {
            expectedTraceId = command!.TraceId.ToString();
            expectedSpanId = command.SpanId.ToString();

            writer.Enqueue(integrationEvent);
        }

        await context.SaveChangesAsync();

        // A second context, so the value is read back out of the database rather than out of the
        // change tracker. The round trip is the point: nvarchar(55) either holds a traceparent
        // or silently fails the INSERT, exactly as nvarchar(500) did for Payload.
        await using var verify = fixture.CreateContext();
        var stored = await verify.OutboxMessages.SingleAsync(m => m.Id == integrationEvent.EventId);

        Assert.NotNull(stored.TraceParent);
        Assert.Equal($"00-{expectedTraceId}-{expectedSpanId}-01", stored.TraceParent);
    }

    [Fact]
    public async Task TheEnqueueIsRecordedAsAnEventOnTheCommandsOwnSpan()
    {
        await using var context = fixture.CreateContext();
        var writer = new OutboxWriter<TracingDbContext>(context);

        using (BoomboCardTelemetry.Source.StartActivity("command Checkout"))
        {
            writer.Enqueue(new TestIntegrationEvent(Guid.NewGuid(), Elapsed));
        }

        await context.SaveChangesAsync();

        var command = _recorder.Single("command Checkout");

        var enqueued = Assert.Single(
            command.Events,
            activityEvent => activityEvent.Name == BoomboCardTelemetry.OutboxEnqueuedEvent);

        Assert.Equal(
            "TestIntegrationEvent",
            enqueued.Tags.Single(tag => tag.Key == BoomboCardTelemetry.MessageTypeTag).Value);

        Assert.Equal(
            "Tracing",
            enqueued.Tags.Single(tag => tag.Key == BoomboCardTelemetry.ModuleTag).Value);
    }

    [Fact]
    public async Task AMessageEnqueuedWithNoActiveSpanStoresNoTraceContext()
    {
        await using var context = fixture.CreateContext();
        var writer = new OutboxWriter<TracingDbContext>(context);

        var integrationEvent = new TestIntegrationEvent(Guid.NewGuid(), Elapsed);

        Assert.Null(Activity.Current);

        writer.Enqueue(integrationEvent);
        await context.SaveChangesAsync();

        await using var verify = fixture.CreateContext();
        var stored = await verify.OutboxMessages.SingleAsync(m => m.Id == integrationEvent.EventId);

        // Null, not a fabricated root. A trace that claims a parent it never had is worse than
        // one that admits it starts here.
        Assert.Null(stored.TraceParent);
        Assert.Null(stored.TraceState);
    }

    // ----------------------------------------------------------------- the consuming side

    /// <summary>
    /// The whole point of the feature. The command's span has ended, the request is over, and a
    /// later poll on another thread with no ambient activity still publishes <b>inside the
    /// original trace</b>.
    /// </summary>
    [Fact]
    public async Task TheDispatcherPublishesInsideTheTraceThatEnqueuedTheMessage()
    {
        var integrationEvent = new TestIntegrationEvent(Guid.NewGuid(), Elapsed);

        ActivityTraceId originalTraceId;
        ActivitySpanId enqueueSpanId;

        await using (var context = fixture.CreateContext())
        {
            var writer = new OutboxWriter<TracingDbContext>(context);

            using (var command = BoomboCardTelemetry.Source.StartActivity("command Checkout"))
            {
                originalTraceId = command!.TraceId;
                enqueueSpanId = command.SpanId;
                writer.Enqueue(integrationEvent);
            }

            await context.SaveChangesAsync();
        }

        // Everything about the producing side is now gone: span disposed, context disposed, no
        // ambient activity. All that remains is a row.
        Assert.Null(Activity.Current);

        var published = await RunDispatcherUntilPublishedAsync();

        // Read off Activity.Current as the publisher saw it — the context every consumer, inbox
        // claim and nested command would actually inherit.
        var observed = Assert.Single(published.ObservedContexts);

        Assert.Equal(originalTraceId, observed.TraceId);

        var publishSpan = PublishSpanFor(integrationEvent.EventId);

        Assert.Equal(originalTraceId, publishSpan.TraceId);
        Assert.Equal(enqueueSpanId, publishSpan.ParentSpanId);

        // Consumer, so a backend draws the gap as an asynchronous handoff rather than as a
        // very slow function call.
        Assert.Equal(ActivityKind.Consumer, publishSpan.Kind);
        Assert.Equal(ActivityStatusCode.Unset, publishSpan.Status);
    }

    /// <summary>
    /// The multi-hop case, which is what "one flow regardless of the process" actually asks for.
    /// A consumer that queues an event of its own extends the same trace rather than starting a
    /// second one — and so does the consumer after that, for as many hops as the cascade runs.
    /// </summary>
    [Fact]
    public async Task AnEventQueuedByAConsumerStaysInTheSameFlow()
    {
        var first = new TestIntegrationEvent(Guid.NewGuid(), Elapsed);
        var second = new TestIntegrationEvent(Guid.NewGuid(), Elapsed);

        ActivityTraceId originalTraceId;

        await using (var context = fixture.CreateContext())
        {
            var writer = new OutboxWriter<TracingDbContext>(context);

            using (var command = BoomboCardTelemetry.Source.StartActivity("command Checkout"))
            {
                originalTraceId = command!.TraceId;
                writer.Enqueue(first);
            }

            await context.SaveChangesAsync();
        }

        // The consumer's own work: while handling the first event, it queues a second one — the
        // Sales-confirms-then-Payments-and-Loyalty-react shape, in miniature.
        //
        // Guarded so it happens once. The dispatcher keeps polling until it is stopped and will
        // deliver the follow-up event too, and enqueuing the same EventId twice is a
        // primary-key violation rather than a second hop.
        var queuedFollowUp = false;

        await RunDispatcherUntilPublishedAsync(onPublish: async _ =>
        {
            if (queuedFollowUp)
            {
                return;
            }

            queuedFollowUp = true;

            await using var consumerContext = fixture.CreateContext();
            var consumerWriter = new OutboxWriter<TracingDbContext>(consumerContext);

            consumerWriter.Enqueue(second);
            await consumerContext.SaveChangesAsync();
        });

        await using var verify = fixture.CreateContext();
        var stored = await verify.OutboxMessages.SingleAsync(m => m.Id == second.EventId);

        Assert.NotNull(stored.TraceParent);

        var restored = TraceContext.Restore(stored.TraceParent, stored.TraceState);

        // Same trace as the original command, two hops later, having passed through a database
        // column twice and two background scopes.
        Assert.Equal(originalTraceId, restored.TraceId);

        // And its parent is the publish span, not the original command — so the waterfall shows
        // the real chain of causation rather than a flat list under the request.
        var publishSpan = PublishSpanFor(first.EventId);
        Assert.Equal(publishSpan.SpanId, restored.SpanId);
    }

    [Fact]
    public async Task AMessageWithNoStoredContextIsStillPublished()
    {
        var integrationEvent = new TestIntegrationEvent(Guid.NewGuid(), Elapsed);

        await using (var context = fixture.CreateContext())
        {
            // Enqueued with no span at all, so the row carries no trace context.
            new OutboxWriter<TracingDbContext>(context).Enqueue(integrationEvent);
            await context.SaveChangesAsync();
        }

        var published = await RunDispatcherUntilPublishedAsync();

        Assert.Single(published.ObservedContexts);

        await using var verify = fixture.CreateContext();
        var stored = await verify.OutboxMessages.SingleAsync(m => m.Id == integrationEvent.EventId);

        // Delivered, and marked delivered. Telemetry is never the reason a message does not go
        // out — the span is simply a new root.
        Assert.Equal(OutboxStatus.Published, stored.Status);

        // And it still appears somewhere findable. With no stored context to restore, the
        // publish span falls back to the ambient activity — which inside the dispatcher is the
        // pass span — so an untraced message is grouped under the poll that delivered it rather
        // than becoming a bare orphan root. That fallback is ActivitySource's own behaviour for
        // a default ActivityContext, and it happens to be the more useful of the two outcomes.
        var publishSpan = PublishSpanFor(integrationEvent.EventId);
        var pass = FirstPassSpan();

        Assert.Equal(pass.SpanId, publishSpan.ParentSpanId);
        Assert.Equal(pass.TraceId, publishSpan.TraceId);
    }

    [Fact]
    public async Task AFailedMessageLeavesASpanMarkedAsFailed()
    {
        var integrationEvent = new TestIntegrationEvent(Guid.NewGuid(), Elapsed);

        await using (var context = fixture.CreateContext())
        {
            using (BoomboCardTelemetry.Source.StartActivity("command Checkout"))
            {
                new OutboxWriter<TracingDbContext>(context).Enqueue(integrationEvent);
            }

            await context.SaveChangesAsync();
        }

        await RunDispatcherUntilPublishedAsync(
            onPublish: _ => throw new InvalidOperationException("the consumer threw"));

        var publishSpan = PublishSpanFor(integrationEvent.EventId);

        // The dispatcher swallows this by design, so that one bad message does not block the
        // batch. The span is what stops "by design" from meaning "invisible".
        Assert.Equal(ActivityStatusCode.Error, publishSpan.Status);
        Assert.Equal("the consumer threw", publishSpan.StatusDescription);

        Assert.Contains(
            publishSpan.Events,
            activityEvent => activityEvent.Name == "exception");

        var pass = FirstPassSpan();
        Assert.Equal(ActivityStatusCode.Error, pass.Status);
        Assert.Equal(0, pass.GetTagItem(BoomboCardTelemetry.OutboxPublishedTag));
        Assert.Equal(1, pass.GetTagItem(BoomboCardTelemetry.OutboxFailedTag));
    }

    /// <summary>
    /// The poller's span is a root with links, not a child of the work it carries. Asserted
    /// because the tempting alternative — parenting the pass to one of its messages — would put
    /// the batch size and failure count of unrelated work inside somebody's checkout trace.
    /// </summary>
    [Fact]
    public async Task ThePassSpanIsARootThatLinksToTheFlowsItServes()
    {
        var integrationEvent = new TestIntegrationEvent(Guid.NewGuid(), Elapsed);

        ActivityTraceId originalTraceId;

        await using (var context = fixture.CreateContext())
        {
            using (var command = BoomboCardTelemetry.Source.StartActivity("command Checkout"))
            {
                originalTraceId = command!.TraceId;
                new OutboxWriter<TracingDbContext>(context).Enqueue(integrationEvent);
            }

            await context.SaveChangesAsync();
        }

        await RunDispatcherUntilPublishedAsync();

        var pass = FirstPassSpan();

        Assert.Equal(default, pass.ParentSpanId);
        Assert.NotEqual(originalTraceId, pass.TraceId);

        var link = Assert.Single(pass.Links);
        Assert.Equal(originalTraceId, link.Context.TraceId);
    }

    // ----------------------------------------------------------------- harness

    /// <summary>
    /// The publish span for one specific message, found by its id rather than by span name.
    ///
    /// <para>
    /// By name would be a latent flake: the dispatcher is a live loop on a 50ms timer, so a
    /// retry or a second message can add another span with the same name between the signal and
    /// the stop, and <c>Single</c> would then fail for a reason that has nothing to do with what
    /// is being asserted.
    /// </para>
    /// </summary>
    private Activity PublishSpanFor(Guid eventId) =>
        Assert.Single(
            _recorder.Recorded,
            activity => activity.OperationName == "outbox publish TestIntegrationEvent"
                && Equals(activity.GetTagItem(BoomboCardTelemetry.MessagingMessageIdTag), eventId));

    /// <summary>The first non-empty pass. Empty passes produce no span at all, by design.</summary>
    private Activity FirstPassSpan() =>
        _recorder.Recorded.First(activity => activity.OperationName == "outbox pass Tracing");

    /// <summary>
    /// Runs the real <see cref="OutboxDispatcher{TContext}"/> as a hosted service until it has
    /// put one message through, then stops it.
    ///
    /// <para>
    /// The real dispatcher rather than a reimplementation of its loop, and a real
    /// <c>PeriodicTimer</c> on a short interval rather than a fake clock: the property under test
    /// is that the trace survives a genuine asynchronous handoff — a different thread, a
    /// different DI scope, no ambient activity — and a test that called a private method
    /// directly would keep the caller's ambient context and pass whether or not propagation
    /// worked at all.
    /// </para>
    /// </summary>
    private async Task<RecordingPublisher> RunDispatcherUntilPublishedAsync(
        Func<object, Task>? onPublish = null)
    {
        var publisher = new RecordingPublisher { OnPublish = onPublish };

        // Armed BEFORE the dispatcher starts, so a pass that finishes quickly is not missed.
        //
        // The wait is on the PASS span closing, not on the publish callback. The callback runs
        // in the middle of the pass, before the message is marked and saved — and stopping the
        // host at that moment cancels the token that same SaveChangesAsync is using, so the row
        // would still read Pending. The pass span closes after the save. (That cancellation is
        // correct behaviour, incidentally: a message interrupted by shutdown stays pending and
        // is redelivered, which is what at-least-once delivery is for.)
        var passCompleted = _recorder.WaitForSpanAsync("outbox pass Tracing");

        var services = new ServiceCollection();
        services.AddDbContext<TracingDbContext>(options => options.UseSqlServer(fixture.ConnectionString));
        services.AddSingleton<IPublisher>(publisher);

        await using var provider = services.BuildServiceProvider();

        var dispatcher = new OutboxDispatcher<TracingDbContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<OutboxDispatcher<TracingDbContext>>.Instance,
            new OutboxOptions { PollInterval = TimeSpan.FromMilliseconds(50), BatchSize = 10 });

        await dispatcher.StartAsync(CancellationToken.None);

        try
        {
            // Generous: this is waiting on a real timer and a real database, and a flaky
            // observability test is worse than none.
            var finished = await Task.WhenAny(passCompleted, Task.Delay(TimeSpan.FromSeconds(30)));

            Assert.Same(passCompleted, finished);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }

        return publisher;
    }
}

/// <summary>
/// Every class in this project belongs to this collection, and it is the
/// <see cref="ActivityRecorder"/> rather than the database that requires it: an
/// <c>ActivityListener</c> is process-wide, so two classes recording spans in parallel would
/// each see the other's and both would fail intermittently on a Single() that found two.
/// </summary>
[CollectionDefinition("tracing", DisableParallelization = true)]
public sealed class TracingCollection;
