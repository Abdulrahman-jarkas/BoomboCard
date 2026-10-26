using System.Diagnostics;

namespace BoomboCard.SharedKernel.Observability;

/// <summary>
/// The one <see cref="ActivitySource"/> this application emits spans from, and the names of the
/// attributes it puts on them.
///
/// <para>
/// It lives in the SharedKernel, with no package reference, because <c>ActivitySource</c> ships
/// in the .NET shared framework — so the project that is "deliberately free of EF Core,
/// ASP.NET Core and any other infrastructure package" stays that way. Nothing here knows what a
/// span is exported to; the host decides that, exactly as it decides the database provider and
/// the payment gateway.
/// </para>
///
/// <para>
/// <b>One source, not one per module.</b> A module-per-source arrangement would mean the host
/// had to enumerate seven source names to collect a trace, and forgetting one would drop that
/// module's spans out of the middle of a flow with nothing to indicate it — the same class of
/// silent gap as a missing DI registration. The module is an <i>attribute</i>
/// (<see cref="ModuleTag"/>) rather than a source, so it can be filtered on without being able
/// to disappear.
/// </para>
/// </summary>
public static class BoomboCardTelemetry
{
    /// <summary>
    /// What the host passes to <c>AddSource</c>. A const, so the host cannot name it slightly
    /// differently and collect nothing — which fails silently, since an ActivitySource with no
    /// listener returns null from StartActivity rather than throwing.
    /// </summary>
    public const string ActivitySourceName = "BoomboCard";

    public static readonly ActivitySource Source = new(ActivitySourceName);

    // ----------------------------------------------------------------- attribute names

    /// <summary>Which bounded context the span belongs to. Derived from the DbContext name.</summary>
    public const string ModuleTag = "boombocard.module";

    /// <summary>The CLR type name of the event, command or query the span is about.</summary>
    public const string MessageTypeTag = "boombocard.message.type";

    /// <summary><c>domain</c> or <c>integration</c> — the two kinds of event in the system.</summary>
    public const string EventKindTag = "boombocard.event.kind";

    /// <summary>How deep into a domain event cascade this event was raised. See DomainEventDispatcher.</summary>
    public const string CascadeDepthTag = "boombocard.event.cascade_depth";

    public const string OutboxAttemptTag = "boombocard.outbox.attempt";

    public const string OutboxStatusTag = "boombocard.outbox.status";

    public const string OutboxBatchSizeTag = "boombocard.outbox.batch_size";

    public const string OutboxPublishedTag = "boombocard.outbox.published";

    public const string OutboxFailedTag = "boombocard.outbox.failed";

    public const string InboxConsumerTag = "boombocard.inbox.consumer";

    /// <summary>
    /// True when a redelivery was recognised and the handler returned without doing the work.
    /// Worth an attribute rather than a log line: at-least-once delivery means this is normal,
    /// and the question a dashboard has to answer is "normal, or stuck in a retry loop".
    /// </summary>
    public const string InboxDuplicateTag = "boombocard.inbox.duplicate";

    public const string JobNameTag = "boombocard.job.name";

    public const string JobOutcomeTag = "boombocard.job.outcome";

    public const string JobItemsProcessedTag = "boombocard.job.items_processed";

    public const string JobItemsFailedTag = "boombocard.job.items_failed";

    public const string RequestKindTag = "boombocard.request.kind";

    // OpenTelemetry's own messaging conventions, so a dashboard that understands them — and
    // every OTLP backend does — groups and labels these spans without being taught our names.
    public const string MessagingSystemTag = "messaging.system";
    public const string MessagingOperationTag = "messaging.operation.type";
    public const string MessagingMessageIdTag = "messaging.message.id";
    public const string MessagingDestinationTag = "messaging.destination.name";

    /// <summary>The value of <see cref="MessagingSystemTag"/> for everything that goes through the outbox.</summary>
    public const string MessagingSystem = "boombocard.outbox";

    // ----------------------------------------------------------------- event names

    public const string OutboxEnqueuedEvent = "outbox.enqueued";
    public const string InboxClaimedEvent = "inbox.claimed";
    public const string InboxDuplicateEvent = "inbox.duplicate";
    public const string DomainEventRaisedEvent = "domain_event.raised";

    /// <summary>
    /// Records an exception on a span and marks it failed, in one call.
    ///
    /// <para>
    /// Exists so that the three places that catch an exception and keep going — the outbox's
    /// per-message handler, the outbox's per-pass boundary, and <c>JobRunner</c> — all leave the
    /// same trace behind. Those catches are deliberate and documented: a failing message must
    /// not block the batch and a failing pass must not stop the loop. The cost of swallowing is
    /// that the failure is invisible unless something records it, and a span with
    /// <see cref="ActivityStatusCode.Error"/> is what makes it show up in a dashboard rather
    /// than only in a log nobody is reading.
    /// </para>
    /// </summary>
    public static void RecordFailure(this Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddException(exception);
    }

    /// <summary>
    /// The short, readable name of a module, taken from its DbContext type —
    /// <c>CatalogDbContext</c> becomes <c>Catalog</c>.
    ///
    /// <para>
    /// Derived rather than configured, for the same reason the architecture tests derive module
    /// namespaces from the module name: a convention that something reads is a convention that
    /// can be relied on, and one that has to be restated per module is one that will eventually
    /// be restated wrongly.
    /// </para>
    /// </summary>
    public static string ModuleNameOf(Type contextType)
    {
        var name = contextType.Name;

        return name.EndsWith("DbContext", StringComparison.Ordinal)
            ? name[..^"DbContext".Length]
            : name;
    }
}
