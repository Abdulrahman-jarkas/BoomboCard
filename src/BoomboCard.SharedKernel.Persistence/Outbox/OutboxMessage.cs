namespace BoomboCard.SharedKernel.Persistence.Outbox;

public enum OutboxStatus
{
    Pending = 0,
    Published = 1,
    DeadLettered = 2
}

/// <summary>
/// An integration event queued for publication, written in the SAME transaction and the SAME
/// database as the state change that produced it. That co-location is the whole point: if the
/// command rolls back, the message goes with it, and if the command commits the message
/// cannot be lost.
///
/// RiverBooks keeps its email outbox in MongoDB while the aggregate is in SQL Server, so the
/// two cannot share a transaction — an order can commit with no queued email, or an email can
/// be queued for an order that rolled back. That is a durable queue, not an outbox.
///
/// Attempt tracking and DeadLettered exist because RiverBooks' poller takes the first
/// unprocessed row, and never marks it on failure — so one permanently failing message is
/// retried forever and blocks every message behind it.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; private set; }

    /// <summary>Assembly-qualified type name, used to rehydrate the payload.</summary>
    public string Type { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public OutboxStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    /// <summary>
    /// The W3C <c>traceparent</c> of the command that produced this message, captured at
    /// enqueue time and restored as the parent of the publish span.
    ///
    /// <para>
    /// This column is what makes a BoomboCard trace one flow. The dispatcher picks this row up
    /// on a later timer tick, on another thread, in another DI scope, with no ambient
    /// <c>Activity</c> — so without a stored parent the publish and everything it causes would
    /// start a second, unconnected trace. See <c>TraceContext</c>.
    /// </para>
    ///
    /// <para>
    /// Nullable, and nothing reads it except telemetry. A message with no trace context is
    /// published exactly like any other — tracing off, or a message enqueued outside any span,
    /// must never be a delivery failure.
    /// </para>
    /// </summary>
    public string? TraceParent { get; private set; }

    /// <summary>Vendor <c>tracestate</c>, propagated verbatim and never interpreted.</summary>
    public string? TraceState { get; private set; }

    private OutboxMessage() { }

    /// <summary>
    /// <paramref name="traceParent"/> and <paramref name="traceState"/> are optional because
    /// they are diagnostics: a caller with no active trace, or a host with tracing switched
    /// off, enqueues a message with both null and it is delivered identically.
    /// </summary>
    public static OutboxMessage Create(
        Guid eventId,
        string type,
        string payload,
        DateTimeOffset occurredAtUtc,
        string? traceParent = null,
        string? traceState = null) =>
        new()
        {
            Id = eventId,
            Type = type,
            Payload = payload,
            Status = OutboxStatus.Pending,
            AttemptCount = 0,
            OccurredAtUtc = occurredAtUtc,
            NextAttemptAtUtc = occurredAtUtc,
            TraceParent = traceParent,
            TraceState = traceState
        };

    public void MarkPublished(DateTimeOffset nowUtc)
    {
        Status = OutboxStatus.Published;
        ProcessedAtUtc = nowUtc;
        NextAttemptAtUtc = null;
        LastError = null;
    }

    /// <summary>
    /// Records a failure and schedules a backoff retry, or dead-letters once the attempt
    /// budget is spent so the message stops blocking the queue and starts being visible.
    /// </summary>
    public void MarkFailed(string error, DateTimeOffset nowUtc, int maxAttempts)
    {
        AttemptCount++;
        LastError = error.Length > 2000 ? error[..2000] : error;

        if (AttemptCount >= maxAttempts)
        {
            Status = OutboxStatus.DeadLettered;
            ProcessedAtUtc = nowUtc;
            NextAttemptAtUtc = null;
            return;
        }

        var backoffSeconds = Math.Min(Math.Pow(2, AttemptCount), 300);
        NextAttemptAtUtc = nowUtc.AddSeconds(backoffSeconds);
    }
}
