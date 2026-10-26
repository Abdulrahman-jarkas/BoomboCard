using BoomboCard.Catalog.Contracts;
using BoomboCard.Sales.Contracts;
using BoomboCard.Sales.Data;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Jobs;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Sales.Application.Reconciliation;

/// <summary>
/// Hunts for stock held against a checkout that no longer exists. <b>Recovery layer 3</b>, the
/// last of the four in <c>CONCEPTS.md</c> §13 — and the one that should find nothing.
///
/// <para>
/// <b>Why layer 2 is not enough.</b> The expiry sweeper enumerates <i>sessions</i>, so it can
/// only release a hold whose session row exists. Sequence the three transactions and there is a
/// window that produces a hold with no session:
/// </para>
///
/// <code>
/// TX 0 session committed -> TX 1 deduction committed -> crash before TX 2
///      layer 2 finds the session, expires it, the release goes out.      FINE
///
/// TX 1 deduction committed -> TX 0 never committed
///      no session row exists. Layer 2 enumerates sessions, so it NEVER sees this.
/// </code>
///
/// <para>
/// The session is committed first precisely to make the second case rare, but "rare" is not
/// "impossible" — a manual rollback, a restored backup out of step with Catalog's, a bug in a
/// future handler. A hold nothing can enumerate is permanently unsellable inventory that nobody
/// will ever notice, which is the exact failure mode Onyx <b>D8</b> describes and the reason
/// Catalog's README recorded this sweep as deferred until Sales existed.
/// </para>
///
/// <para>
/// <b>A two-query handshake, because neither module can answer alone.</b> Catalog can list
/// references whose stock nets negative; it cannot tell a completed sale from an orphan, because
/// the reference is a session id it does not own. Sales knows what every session id means; it
/// cannot enumerate holds, because it does not own stock. So Catalog reports, Sales decides, and
/// the decision is a command back into Catalog — no shared table, no module reaching into
/// another's schema, and the <c>UnreconciledStockHold</c>/<c>CheckoutSessionOutcome</c> pair is
/// the whole of the coupling.
/// </para>
///
/// <para>
/// <b>What it does with each verdict</b> is where the care goes, because this job can destroy
/// real inventory data if it guesses:
/// </para>
///
/// <list type="bullet">
/// <item><b>Session confirmed</b> — a completed sale. The stock is <i>meant</i> to be gone.
/// Leave it. This is the common case, and getting it wrong would hand free stock back for every
/// order ever placed.</item>
/// <item><b>Session still live</b> — a customer is checking out right now. Leave it; the grace
/// period should already have excluded this, so it is logged as a surprise.</item>
/// <item><b>Session expired or cancelled</b> — layer 2 should have released it. Release it and
/// log a warning: the hold is recoverable, but layer 2 failing silently is a defect worth
/// seeing.</item>
/// <item><b>No session at all</b> — the true orphan. Release it and log an error. Impossible
/// under the designed ordering, so if it happens the ordering assumption is wrong somewhere and
/// a human needs to know.</item>
/// </list>
///
/// <para>
/// It never invents a quantity. Every release is the exact negative net Catalog reported, handed
/// to <c>RestoreStockCommand</c>, which is idempotent on the same reference — so this job
/// racing an in-flight layer-2 release is harmless. Contrast Onyx <b>D5</b>: a job that topped
/// stock up to a level it computed itself, healing every oversell before anyone saw one.
/// </para>
/// </summary>
internal sealed class OrphanSaleReconciliationJob(
    JobRunner<SalesDbContext> runner,
    SalesOptions options,
    TimeProvider timeProvider,
    ILogger<OrphanSaleReconciliationJob> logger) : BackgroundService
{
    public const string JobName = "sales.orphan-reconciliation";

    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.OrphanSweepInterval, timeProvider);

        logger.LogInformation(
            "Orphan-sale reconciliation started, running every {Interval} over holds older than "
            + "{Grace}.",
            options.OrphanSweepInterval,
            options.OrphanGracePeriod);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, SweepAsync, stoppingToken);
        }
    }

    private async Task<JobResult> SweepAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var sender = services.GetRequiredService<ISender>();
        var dispatcher = services.GetRequiredService<ICommandDispatcher>();

        var cutoff = timeProvider.GetUtcNow() - options.OrphanGracePeriod;

        var holds = await sender.Send(
            new GetUnreconciledStockHoldsQuery(cutoff, BatchSize), cancellationToken);

        if (holds.IsError)
        {
            logger.LogError(
                "Could not list unreconciled stock holds: {Error}", holds.FirstError.Description);

            return JobResult.Nothing;
        }

        if (holds.Value.Count == 0)
        {
            return JobResult.Nothing;
        }

        var outcomes = await sender.Send(
            new GetCheckoutOutcomesQuery(holds.Value.Select(hold => hold.Reference).ToList()),
            cancellationToken);

        if (outcomes.IsError)
        {
            logger.LogError(
                "Could not resolve checkout outcomes: {Error}", outcomes.FirstError.Description);

            return JobResult.Nothing;
        }

        var byReference = outcomes.Value.ToDictionary(outcome => outcome.Reference);

        var released = 0;
        var failed = 0;

        foreach (var hold in holds.Value)
        {
            if (!byReference.TryGetValue(hold.Reference, out var outcome))
            {
                failed++;
                continue;
            }

            if (outcome.IsConfirmed)
            {
                // A completed sale. The stock is supposed to be gone.
                continue;
            }

            if (outcome.IsLive)
            {
                // Past the grace period and still live. Possible only if a session's deadline
                // was extended, which nothing currently does — so it is worth saying out loud
                // rather than skipping quietly.
                logger.LogWarning(
                    "Checkout {Reference} has held stock since {HeldSince} and is still live. "
                    + "Left alone.",
                    hold.Reference,
                    hold.HeldSinceUtc);

                continue;
            }

            if (outcome.Exists)
            {
                logger.LogWarning(
                    "Checkout {Reference} is finished but its stock was never released. Layer 2 "
                    + "missed it; releasing {LineCount} line(s) now.",
                    hold.Reference,
                    hold.Lines.Count);
            }
            else
            {
                logger.LogError(
                    "Stock has been held since {HeldSince} against reference {Reference}, which "
                    + "matches no checkout session. The session commits before the deduction, so "
                    + "this should be impossible — releasing {LineCount} line(s) and flagging "
                    + "the ordering assumption.",
                    hold.HeldSinceUtc,
                    hold.Reference,
                    hold.Lines.Count);
            }

            try
            {
                // Exactly what Catalog reported, never a figure this job computed. Idempotent
                // on the reference, so racing an in-flight layer-2 release is a no-op.
                var restored = await dispatcher.SendAsync(
                    new RestoreStockCommand(
                        hold.Reference, RestoreReason.SessionExpired, hold.Lines),
                    cancellationToken);

                if (restored.IsError)
                {
                    logger.LogError(
                        "Could not restore the stock held against {Reference}: {Error}",
                        hold.Reference,
                        restored.FirstError.Description);

                    failed++;
                    continue;
                }

                released++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(
                    exception,
                    "Could not restore the stock held against {Reference}.",
                    hold.Reference);

                failed++;
            }
        }

        return new JobResult(released, failed);
    }
}
