using System.Diagnostics;
using BoomboCard.SharedKernel.Observability;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BoomboCard.Api.Observability;

/// <summary>
/// Options for what the host does with the telemetry the rest of the solution emits.
///
/// <para>
/// Bound from the <c>Observability</c> section. Every value has a default that works, because
/// the one thing this must not do is make the application refuse to start over a dashboard —
/// compare the connection strings, which throw on absence because an application with no
/// database is not running in any useful sense.
/// </para>
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>
    /// What a trace is labelled with in the dashboard. One name for the whole host, because it
    /// IS one process — the modules are <c>boombocard.module</c> attributes on spans, not
    /// separate services. Calling them separate services would split one checkout's waterfall
    /// across seven service names and make a modular monolith look like a distributed system it
    /// is not.
    /// </summary>
    public string ServiceName { get; init; } = "boombocard-api";

    /// <summary>
    /// Where spans are sent. The default is the OTLP/gRPC port the Aspire dashboard, the
    /// OpenTelemetry Collector, Jaeger and Tempo all listen on, so the compose file in this repo
    /// needs no configuration to work.
    /// </summary>
    public string OtlpEndpoint { get; init; } = "http://localhost:4317";

    /// <summary>
    /// Off means no exporter and no listener — and therefore, because
    /// <c>ActivitySource.StartActivity</c> returns null when nothing is subscribed, no
    /// allocations and no activities anywhere in the solution. The instrumentation is written to
    /// treat that as normal rather than as a disabled feature: an outbox row simply gets null
    /// trace columns and is delivered identically.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Fraction of traces kept, 0 to 1. One is the default and is right for development and for
    /// a system at this traffic level.
    ///
    /// <para>
    /// Whatever it is set to, it is applied <b>parent-based</b>: a span that continues an
    /// existing trace follows that trace's decision rather than re-rolling the dice. That is the
    /// difference between sampling a flow and shredding it — a fresh 10% decision at the outbox
    /// would discard nine tenths of the downstream half of flows whose first half was kept, and
    /// those truncated traces are exactly the ones that look like a bug in the outbox.
    /// </para>
    /// </summary>
    public double SampleRatio { get; init; } = 1.0;

    /// <summary>
    /// Also send logs over OTLP, so a span and the log lines written inside it sit together in
    /// one place. Onyx's recurring failure mode was information that existed and was unreachable
    /// — a worker status panel reading a different process's static field, warnings on every
    /// compile that nobody saw.
    /// </summary>
    public bool ExportLogs { get; init; } = true;

    public bool ExportMetrics { get; init; } = true;
}

public static class ObservabilityComposition
{
    /// <summary>
    /// Wires OpenTelemetry for the host: the ASP.NET Core and HttpClient instrumentation, the
    /// solution's own <see cref="BoomboCardTelemetry.Source"/>, and an OTLP exporter.
    ///
    /// <para>
    /// <b>Why this lives in the host and not in the SharedKernel.</b> Same reason the EF
    /// provider, the JWT signing key, the payment gateway and the notification sender do: the
    /// endpoint, the credential and the sampling rate are deployment decisions. The modules emit
    /// spans through an <c>ActivitySource</c> from the shared framework and have no idea whether
    /// anything is listening — which is also what lets the architecture test
    /// <c>SharedKernel_DoesNotDependOnInfrastructure</c> keep passing with the whole event flow
    /// instrumented.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBoomboCardObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection("Observability").Get<ObservabilityOptions>()
            ?? new ObservabilityOptions();

        services.AddSingleton(options);

        if (!options.Enabled)
        {
            return services;
        }

        // W3C ids, forced. This is the default in modern .NET, but TraceContext refuses to write
        // a traceparent for an activity in the legacy hierarchical format — correctly, since its
        // TraceId and SpanId fields read as all-zeroes — and a silently unpropagated trace is
        // the failure this whole exercise exists to remove. Stating it here means the only way
        // to get a hierarchical activity is for somebody to deliberately override this.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: options.ServiceName,
                serviceVersion: typeof(ObservabilityComposition).Assembly.GetName().Version?.ToString(),

                // So two instances behind a load balancer are distinguishable in a trace. The
                // machine name is the honest answer in a deployment this shape.
                serviceInstanceId: Environment.MachineName)
            .AddAttributes([new KeyValuePair<string, object>("deployment.environment", environment.EnvironmentName)]);

        var builder = services.AddOpenTelemetry().WithTracing(tracing => tracing
            .SetResourceBuilder(resource)

            // ParentBased, always. See ObservabilityOptions.SampleRatio: a per-span decision
            // would cut flows in half at exactly the handoff this design exists to stitch
            // together. With a ratio of 1 the wrapper is a no-op; it is written this way so
            // turning sampling down later cannot quietly start truncating traces.
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.SampleRatio)))

            // The one source every module emits through. If this name and the const in
            // BoomboCardTelemetry ever diverge, every span in the event flow silently
            // disappears — which is why it is a const and not a string here.
            .AddSource(BoomboCardTelemetry.ActivitySourceName)

            .AddAspNetCoreInstrumentation(instrumentation =>
            {
                instrumentation.RecordException = true;

                // The health endpoint is polled by a liveness probe every few seconds forever
                // and tells nobody anything. Dropped at the source rather than filtered in the
                // dashboard, because the cheapest span is the one never created.
                instrumentation.Filter = httpContext =>
                    !httpContext.Request.Path.StartsWithSegments("/health")
                    && !httpContext.Request.Path.StartsWithSegments("/openapi")
                    && !httpContext.Request.Path.StartsWithSegments("/swagger");
            })

            // Outgoing calls: the payment gateway and the HTTP notification sender. Worth having
            // in the same trace as the command that caused them — Onyx A12 held a database
            // transaction open across three gateway calls and A14 built a new HttpClient per
            // retry attempt, and both are things you see immediately in a waterfall and barely
            // at all in logs.
            .AddHttpClientInstrumentation(instrumentation => instrumentation.RecordException = true)

            .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint)));

        if (options.ExportMetrics)
        {
            builder.WithMetrics(metrics => metrics
                .SetResourceBuilder(resource)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint)));
        }

        if (options.ExportLogs)
        {
            services.AddLogging(logging => logging.AddOpenTelemetry(telemetry =>
            {
                telemetry.SetResourceBuilder(resource);

                // Both on, because the existing log statements are already structured and
                // deliberately parameterised — "Outbox message {MessageId} of type {MessageType}
                // failed on attempt {Attempt}" is worth far more as queryable fields than as a
                // formatted sentence. Each record also carries the current trace and span id, so
                // a log line and the span it was written inside find each other.
                telemetry.IncludeScopes = true;
                telemetry.IncludeFormattedMessage = true;

                telemetry.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
            }));
        }

        return services;
    }
}
