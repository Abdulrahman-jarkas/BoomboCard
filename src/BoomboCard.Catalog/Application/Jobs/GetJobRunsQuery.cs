using BoomboCard.Catalog.Data;
using BoomboCard.SharedKernel.Persistence.Jobs;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Application.Jobs;

/// <summary>
/// One execution of one background job. An <c>Outcome</c> of <c>Running</c> on an old row means
/// the job died mid-pass — the row is written before the work starts precisely so that is
/// visible.
/// </summary>
public sealed record JobRunResponse(
    Guid Id,
    string JobName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string Outcome,
    int ItemsProcessed,
    int ItemsFailed,
    long? DurationMs,
    string? Error);

/// <summary>
/// The replacement for Onyx's worker-status panel (A7), which read <c>static</c> fields written
/// in a different process and therefore returned null and zero from the day it shipped — while
/// its architecture document described it as "a good example" of shared state that "reflects
/// reality regardless of which API instance happens to serve that request".
///
/// These are rows in the same database every instance reads, so the answer is the same whichever
/// instance serves the request. And a job that dies mid-pass leaves a row stuck in
/// <c>Running</c> rather than leaving no trace at all.
/// </summary>
public sealed record GetJobRunsQuery(string? JobName = null, int Limit = 20)
    : IRequest<ErrorOr<List<JobRunResponse>>>
{
    public const int MaxLimit = 200;
}

internal sealed class GetJobRunsQueryHandler(CatalogDbContext context)
    : IRequestHandler<GetJobRunsQuery, ErrorOr<List<JobRunResponse>>>
{
    public async Task<ErrorOr<List<JobRunResponse>>> Handle(
        GetJobRunsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > GetJobRunsQuery.MaxLimit)
        {
            return Error.Validation(
                "GetJobRuns.InvalidLimit",
                $"Limit must be between 1 and {GetJobRunsQuery.MaxLimit}.");
        }

        var runs = context.JobRuns.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.JobName))
        {
            var name = query.JobName.Trim();
            runs = runs.Where(run => run.JobName == name);
        }

        return await runs
            .OrderByDescending(run => run.StartedAtUtc)
            .Take(query.Limit)
            .Select(run => new JobRunResponse(
                run.Id,
                run.JobName,
                run.StartedAtUtc,
                run.CompletedAtUtc,
                run.Outcome.ToString(),
                run.ItemsProcessed,
                run.ItemsFailed,
                run.DurationMs,
                run.Error))
            .ToListAsync(cancellationToken);
    }
}
