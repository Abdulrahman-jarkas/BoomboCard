using System.Diagnostics;
using BoomboCard.SharedKernel.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BoomboCard.SharedKernel.Persistence.Jobs;

/// <summary>
/// Runs a job body once, recording the attempt and its outcome.
///
/// Every background job goes through this, so three things are guaranteed rather than
/// remembered:
///
///  1. <b>A run row exists before the work starts</b>, so a job that dies mid-pass leaves a row
///     stuck in <c>Running</c> — visible, rather than invisible.
///  2. <b>The outcome is recorded after awaiting the work</b>, with real counts. Onyx A22 logged
///     "pass complete for {Count} products" before any work had run, because the work was
///     <c>async void</c> — the message was identical whether it succeeded, failed or never
///     happened.
///  3. <b>Nothing escapes.</b> An exception is recorded against the run and swallowed at this
///     boundary, so one bad pass cannot stop the loop or the host. Onyx A18/A21: an unhandled
///     DivideByZeroException in the nightly report terminated the whole worker process under the
///     default StopHost behaviour, taking the unrelated stock job with it, with nothing to
///     restart either and no way to find out.
///  4. <b>A root span per run</b>, which is where a job-initiated flow starts.
///
/// <para>
/// (4) is the other half of what the HTTP pipeline gives a request. Not every flow in this
/// system begins with somebody calling an endpoint: the expiry sweeper releases stock holds, the
/// stock integrity check reconciles movements, and each of those mutates aggregates, raises
/// domain events, writes outbox rows and so sets off the same cross-module cascade a checkout
/// does. Those flows need a root, and this is it — so "what did last night's sweeper actually
/// set in motion?" is one trace, on equal footing with "what did this checkout set in motion?".
/// </para>
///
/// <para>
/// Root, with no parent: a job's cause is a timer. The run id goes on the span, so a row in
/// <c>JobRuns</c> and a trace in the dashboard can be matched up from either direction.
/// </para>
/// </summary>
public sealed class JobRunner<TContext>(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<JobRunner<TContext>> logger)
    where TContext : DbContext
{
    public async Task<JobOutcome> RunAsync(
        string jobName,
        Func<IServiceProvider, CancellationToken, Task<JobResult>> body,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        var run = JobRun.Start(jobName, timeProvider.GetUtcNow());

        // Started before the run row is written, so the span covers the bookkeeping too. A job
        // that cannot even record that it started is a failure worth seeing in the same place as
        // every other failure.
        using var activity = BoomboCardTelemetry.Source.StartActivity(
            $"job {jobName}",
            ActivityKind.Internal,
            parentContext: default);

        activity?.SetTag(BoomboCardTelemetry.ModuleTag, BoomboCardTelemetry.ModuleNameOf(typeof(TContext)));
        activity?.SetTag(BoomboCardTelemetry.JobNameTag, jobName);
        activity?.SetTag("boombocard.job.run_id", run.Id);

        context.Set<JobRun>().Add(run);
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            var result = await body(scope.ServiceProvider, cancellationToken);

            run.Succeed(result.ItemsProcessed, result.ItemsFailed, timeProvider.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken);

            activity?.SetTag(BoomboCardTelemetry.JobOutcomeTag, JobOutcome.Succeeded.ToString());
            activity?.SetTag(BoomboCardTelemetry.JobItemsProcessedTag, result.ItemsProcessed);
            activity?.SetTag(BoomboCardTelemetry.JobItemsFailedTag, result.ItemsFailed);

            if (result.ItemsFailed > 0)
            {
                // A pass that processed some and failed some is a success as far as the loop is
                // concerned, and not one a dashboard should show as green.
                activity?.SetStatus(
                    ActivityStatusCode.Error,
                    $"{result.ItemsFailed} item(s) failed.");
            }

            // Only noisy when something actually happened.
            if (result.ItemsProcessed > 0 || result.ItemsFailed > 0)
            {
                logger.LogInformation(
                    "{JobName} completed in {DurationMs}ms: {Processed} processed, {Failed} failed.",
                    jobName,
                    run.DurationMs,
                    result.ItemsProcessed,
                    result.ItemsFailed);
            }

            return JobOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Swallowed at this boundary by design — see (3). The span is what keeps that from
            // meaning "invisible": the trace ends in error even though the loop carries on.
            activity.RecordFailure(exception);
            activity?.SetTag(BoomboCardTelemetry.JobOutcomeTag, JobOutcome.Failed.ToString());

            logger.LogError(exception, "{JobName} failed.", jobName);

            try
            {
                run.Fail(exception.ToString(), timeProvider.GetUtcNow());
                await context.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception recordingFailure)
            {
                // If we cannot even record the failure, log it — never swallow silently. Onyx
                // A16's empty catch blocks are how its payment failures became invisible.
                logger.LogError(recordingFailure, "Could not record the failure of {JobName}.", jobName);
            }

            return JobOutcome.Failed;
        }
    }
}

public sealed class JobRunConfiguration : IEntityTypeConfiguration<JobRun>
{
    public void Configure(EntityTypeBuilder<JobRun> builder)
    {
        builder.ToTable("JobRuns");

        builder.HasKey(run => run.Id);

        builder.Property(run => run.JobName).HasMaxLength(200).IsRequired();
        builder.Property(run => run.StartedAtUtc).IsRequired();
        builder.Property(run => run.Outcome).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(run => run.Error).HasMaxLength(2000);

        // "When did this job last run, and did it work?" — newest first, per job.
        builder.HasIndex(run => new { run.JobName, run.StartedAtUtc })
            .HasDatabaseName("IX_JobRuns_History");
    }
}
