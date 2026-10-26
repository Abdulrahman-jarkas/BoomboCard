namespace BoomboCard.SharedKernel.Persistence.Jobs;

public enum JobOutcome
{
    Running = 0,
    Succeeded = 1,
    Failed = 2
}

/// <summary>
/// A persisted record of one execution of one background job.
///
/// This exists because of Onyx A7, which is my favourite bug in that codebase. It had an admin
/// panel at <c>GET /api/admin/worker-status</c> showing when the overnight jobs last ran. The
/// jobs wrote their status into a <c>static</c> class in the Worker process; the endpoint read
/// the same <c>static</c> class in the API process. Two processes, two separate statics. The
/// panel returned null and zero from the day it shipped and always would — and its architecture
/// document described it as "a good example" of shared state that "reflects reality".
///
/// So it was also the monitoring that would have revealed A18 and D7, where an unhandled
/// exception terminated the whole worker process. The jobs could be dead for weeks, the panel
/// would look exactly as it always had, and the last log line said everything completed fine
/// (A22 — it logged success before doing the work).
///
/// Rows in a table, in the same database as everything else. Any instance can read them.
/// </summary>
public sealed class JobRun
{
    public Guid Id { get; private set; }

    public string JobName { get; private set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public JobOutcome Outcome { get; private set; }

    public int ItemsProcessed { get; private set; }

    public int ItemsFailed { get; private set; }

    public long? DurationMs { get; private set; }

    public string? Error { get; private set; }

    private JobRun() { }

    public static JobRun Start(string jobName, DateTimeOffset nowUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            JobName = jobName,
            StartedAtUtc = nowUtc,
            Outcome = JobOutcome.Running
        };

    public void Succeed(int itemsProcessed, int itemsFailed, DateTimeOffset nowUtc)
    {
        Outcome = JobOutcome.Succeeded;
        ItemsProcessed = itemsProcessed;
        ItemsFailed = itemsFailed;
        Complete(nowUtc);
    }

    public void Fail(string error, DateTimeOffset nowUtc)
    {
        Outcome = JobOutcome.Failed;
        Error = error.Length > 2000 ? error[..2000] : error;
        Complete(nowUtc);
    }

    private void Complete(DateTimeOffset nowUtc)
    {
        CompletedAtUtc = nowUtc;
        DurationMs = (long)(nowUtc - StartedAtUtc).TotalMilliseconds;
    }
}

/// <summary>What a job body reports back. Counts are recorded on the run.</summary>
public sealed record JobResult(int ItemsProcessed, int ItemsFailed = 0)
{
    public static readonly JobResult Nothing = new(0);
}
