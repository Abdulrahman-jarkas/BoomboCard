using System.Diagnostics;

namespace BoomboCard.SharedKernel.Observability;

/// <summary>
/// Carries a trace across an asynchronous handoff, by writing the W3C <c>traceparent</c> and
/// <c>tracestate</c> down with the message and reading them back when it is processed.
///
/// <para>
/// <b>This is the whole reason a BoomboCard trace is one flow rather than several.</b> A span's
/// parent normally comes from <see cref="Activity.Current"/>, which is ambient on the async
/// call stack. The outbox deliberately breaks that call stack: a command writes a row and
/// returns, the HTTP response goes out, and some seconds later a <c>PeriodicTimer</c> tick in
/// <c>OutboxDispatcher</c> picks the row up on a different thread, in a different DI scope, with
/// no ambient activity at all. Without something to carry the context, that tick starts a brand
/// new trace — so the dashboard would show "POST /checkout" and "publish
/// CheckoutConfirmedIntegrationEvent" as two unrelated flows, and the question anyone actually
/// has ("what did this checkout set in motion?") would have no answer.
/// </para>
///
/// <para>
/// Propagating through the message is also what makes this work when the dispatcher is NOT in
/// the same process. Nothing here assumes it is. Today the modular monolith runs one host, and
/// the handoff is already across a thread, a scope and a clock tick; the day a module moves out
/// to its own service, the context travels in a database column it already had, and the trace
/// does not change shape.
/// </para>
///
/// <para>
/// The format is W3C Trace Context, not an invented one, because it is what every OTLP backend
/// and every other instrumented library already speaks — including the ASP.NET Core and
/// HttpClient instrumentation in this host. A bespoke pair of columns would have needed a
/// bespoke propagator at every edge.
/// </para>
/// </summary>
public static class TraceContext
{
    /// <summary><c>00-</c> + 32 hex + <c>-</c> + 16 hex + <c>-</c> + 2 hex = 55 characters, always.</summary>
    public const int TraceParentMaxLength = 55;

    /// <summary>The length the W3C spec tells implementations to accept.</summary>
    public const int TraceStateMaxLength = 512;

    /// <summary>
    /// The current trace context as a W3C <c>traceparent</c>, or null when there is nothing to
    /// capture.
    ///
    /// <para>
    /// Null is a normal answer, not a failure: it is what happens when no exporter is listening
    /// — <c>ActivitySource.StartActivity</c> returns null if nothing has subscribed, so a host
    /// with tracing switched off produces no activities and writes no trace columns. The
    /// consumer side treats null as "start a new trace", so the system works identically with
    /// observability off, which is the only acceptable behaviour for plumbing that sits in the
    /// middle of every command.
    /// </para>
    /// </summary>
    public static string? CaptureTraceParent(Activity? activity = null)
    {
        activity ??= Activity.Current;

        // Hierarchical ids have no TraceId or SpanId — their W3C fields read as all-zeroes,
        // which is a syntactically valid traceparent that no backend will join. Writing one
        // would be worse than writing nothing, because it looks like propagation worked. The
        // host sets ForceDefaultIdFormat so this branch should not be reachable in practice;
        // it is here because "should not be reachable" is not a guarantee.
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C)
        {
            return null;
        }

        var flags = activity.Context.TraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00";

        return $"00-{activity.TraceId}-{activity.SpanId}-{flags}";
    }

    /// <summary>
    /// The current <c>tracestate</c>, which carries vendor-specific data added by other systems
    /// upstream. Propagated without being interpreted — that is what the spec asks of anything
    /// in the middle of a trace, and dropping it would silently break whoever put it there.
    /// </summary>
    public static string? CaptureTraceState(Activity? activity = null)
    {
        var state = (activity ?? Activity.Current)?.TraceStateString;

        return string.IsNullOrWhiteSpace(state) ? null : state;
    }

    /// <summary>
    /// Turns stored trace headers back into a parent context.
    ///
    /// <para>
    /// <paramref name="remote"/> defaults to true because by the time this is called the parent
    /// span has already ended, on another thread and possibly in another process. Saying so
    /// matters: a sampler asked to decide about this span can see it continues an existing,
    /// already-sampled trace rather than starting one, so <c>ParentBasedSampler</c> keeps the
    /// downstream half of a flow whose first half was kept. Claiming it was local would make
    /// head sampling able to tear a flow in two.
    /// </para>
    ///
    /// <para>
    /// Returns <c>default</c> on anything unparseable rather than throwing. A malformed
    /// traceparent means the span becomes a new root — a trace that starts in the wrong place,
    /// which is a reporting defect. Throwing would mean a malformed <i>diagnostic</i> column
    /// could fail an integration event and eventually dead-letter it, turning a reporting defect
    /// into lost business work. Telemetry is never allowed to be the reason something does not
    /// get delivered.
    /// </para>
    /// </summary>
    public static ActivityContext Restore(string? traceParent, string? traceState, bool remote = true) =>
        ActivityContext.TryParse(traceParent, traceState, remote, out var context)
            ? context
            : default;
}
