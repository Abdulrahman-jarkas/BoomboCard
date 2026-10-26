using System.Diagnostics;
using BoomboCard.SharedKernel.Observability;

namespace BoomboCard.Observability.Tests;

/// <summary>
/// The propagation primitive on its own, without a database in the way.
///
/// <para>
/// These are the smallest statements of the claim the whole feature rests on: a context can be
/// written down, read back, and used as a parent, and the resulting span is in the same trace as
/// the one that wrote it. <see cref="OutboxTraceContinuityTests"/> then asserts the same thing
/// through real SQL columns and the real dispatcher.
/// </para>
/// </summary>
[Collection("tracing")]
public sealed class TraceContextTests : IDisposable
{
    private readonly ActivityRecorder _recorder = new();

    public void Dispose() => _recorder.Dispose();

    [Fact]
    public void ACapturedTraceParentIsAWellFormedW3CHeader()
    {
        using var activity = BoomboCardTelemetry.Source.StartActivity("command Thing");

        Assert.NotNull(activity);

        var traceParent = TraceContext.CaptureTraceParent();

        Assert.NotNull(traceParent);

        // Exactly the length the column is declared as. The assertion is on the constant rather
        // than on 55 so that the column, the spec and this test cannot drift apart.
        Assert.Equal(TraceContext.TraceParentMaxLength, traceParent!.Length);

        // version-traceid-spanid-flags, and the ids are the activity's own.
        Assert.Equal(
            $"00-{activity.TraceId}-{activity.SpanId}-01",
            traceParent);
    }

    [Fact]
    public void TheRecordedFlagSurvivesTheRoundTrip()
    {
        using var activity = BoomboCardTelemetry.Source.StartActivity("command Thing");

        // The recorder samples AllDataAndRecorded, so this trace is one a backend keeps.
        Assert.True(activity!.Recorded);

        var restored = TraceContext.Restore(TraceContext.CaptureTraceParent(), null);

        // If this flag were dropped, a ParentBased sampler downstream would decide the
        // continuation was not worth keeping and the second half of every flow would vanish —
        // which looks exactly like the outbox having failed to deliver anything.
        Assert.True(restored.TraceFlags.HasFlag(ActivityTraceFlags.Recorded));
    }

    /// <summary>
    /// The claim, in one test: a span created from a restored context is in the same trace as the
    /// span that was captured, and is its direct child — <b>after that span has already ended</b>
    /// and with no ambient activity anywhere.
    /// </summary>
    [Fact]
    public void ASpanBuiltFromARestoredContextContinuesTheOriginalTrace()
    {
        string? traceParent;
        ActivityTraceId originalTraceId;
        ActivitySpanId originalSpanId;

        using (var producer = BoomboCardTelemetry.Source.StartActivity("command Produce"))
        {
            originalTraceId = producer!.TraceId;
            originalSpanId = producer.SpanId;
            traceParent = TraceContext.CaptureTraceParent();
        }

        // The producing span has been disposed and the ambient context is gone. This is the state
        // the outbox dispatcher genuinely starts from: a later timer tick with nothing on the
        // async call stack.
        Assert.Null(Activity.Current);

        var parent = TraceContext.Restore(traceParent, null);

        using var consumer = BoomboCardTelemetry.Source.StartActivity(
            "outbox publish Thing",
            ActivityKind.Consumer,
            parent);

        Assert.NotNull(consumer);
        Assert.Equal(originalTraceId, consumer!.TraceId);
        Assert.Equal(originalSpanId, consumer.ParentSpanId);
    }

    [Fact]
    public void ARestoredParentIsMarkedRemote()
    {
        using var producer = BoomboCardTelemetry.Source.StartActivity("command Produce");

        var restored = TraceContext.Restore(TraceContext.CaptureTraceParent(), null);

        // Remote is what tells a sampler this span continues a trace rather than starting one.
        // See TraceContext.Restore.
        Assert.True(restored.IsRemote);
    }

    [Fact]
    public void TraceStateIsCarriedWithoutBeingInterpreted()
    {
        using var producer = BoomboCardTelemetry.Source.StartActivity("command Produce");
        producer!.TraceStateString = "vendor=opaque-value";

        var captured = TraceContext.CaptureTraceState();

        Assert.Equal("vendor=opaque-value", captured);

        var restored = TraceContext.Restore(TraceContext.CaptureTraceParent(), captured);

        Assert.Equal("vendor=opaque-value", restored.TraceState);
    }

    [Fact]
    public void NothingIsCapturedWhenThereIsNoActiveSpan()
    {
        Assert.Null(Activity.Current);

        // The normal state with tracing switched off, and not an error: the outbox row simply
        // gets null columns and is delivered identically.
        Assert.Null(TraceContext.CaptureTraceParent());
        Assert.Null(TraceContext.CaptureTraceState());
    }

    [Fact]
    public void AnEmptyTraceStateIsStoredAsNullRatherThanAsBlankText()
    {
        using var producer = BoomboCardTelemetry.Source.StartActivity("command Produce");

        // Activity reports an unset tracestate as null or empty depending on how it was set.
        // Normalising to null keeps the column honest: a blank string reads as "there was a
        // tracestate and it said nothing", which is a different and untrue claim.
        Assert.Null(TraceContext.CaptureTraceState());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-traceparent")]
    [InlineData("00-tooshort-0000000000000000-01")]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7")]
    public void AnUnusableTraceParentYieldsNoParentRatherThanAnException(string? traceParent)
    {
        // Telemetry must never be the reason a business message fails. A malformed diagnostic
        // column costs a correctly-rooted trace; throwing here would cost the delivery, and
        // after enough retries the message itself. See TraceContext.Restore.
        var restored = TraceContext.Restore(traceParent, null);

        Assert.Equal(default, restored);
    }

    /// <summary>
    /// A <c>traceparent</c> from a future version of the spec is joined, not discarded.
    ///
    /// <para>
    /// This is the W3C forward-compatibility rule and it is <c>ActivityContext.TryParse</c>'s
    /// behaviour rather than ours — asserted because it is the opposite of what "unrecognised
    /// version" intuitively suggests, and because getting it wrong in the other direction would
    /// orphan every trace arriving from a newer caller.
    /// </para>
    /// </summary>
    [Fact]
    public void ATraceParentFromALaterSpecVersionIsStillJoined()
    {
        var restored = TraceContext.Restore(
            "99-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            null);

        Assert.NotEqual(default, restored);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", restored.TraceId.ToString());
        Assert.Equal("00f067aa0ba902b7", restored.SpanId.ToString());
    }

    [Fact]
    public void AnAllZeroTraceParentIsNotTreatedAsATrace()
    {
        // The shape a hierarchical-format activity would produce if TraceContext did not refuse
        // to serialise one. It parses as syntactically valid and is not a trace, which is why
        // the dispatcher's link builder filters on `!= default` rather than on null.
        var restored = TraceContext.Restore(
            "00-00000000000000000000000000000000-0000000000000000-00",
            null);

        Assert.Equal(default, restored);
    }

    [Fact]
    public void ModuleNamesAreDerivedFromTheContextType()
    {
        Assert.Equal("Tracing", BoomboCardTelemetry.ModuleNameOf(typeof(TracingDbContext)));

        // A type that is not named *DbContext is used as-is rather than mangled.
        Assert.Equal("TraceContextTests", BoomboCardTelemetry.ModuleNameOf(typeof(TraceContextTests)));
    }
}
