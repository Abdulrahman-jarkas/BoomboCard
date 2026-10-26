using BoomboCard.Customers.Data;
using BoomboCard.SharedKernel.Persistence.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Customers.Application.Maintenance;

/// <summary>
/// Housekeeping for the Customers module. Three sweeps in one pass, each of which exists because
/// keeping the data would be a liability rather than an asset.
///
///  1. <b>Expired and spent credential tokens are deleted.</b> A redeemed reset token is a
///     stored hash of a credential that can no longer do anything; keeping it is pure downside.
///  2. <b>Auth attempts past their retention window are deleted.</b> The rows contain email
///     addresses, including ones typed by people with no account here, so "keep them forever"
///     is not a neutral default. See <c>CustomersOptions.AuthAttemptRetention</c>.
///  3. <b>Published and dead-lettered outbox messages are deleted.</b> This module's outbox
///     rows can contain a plaintext verification or reset token (see
///     <c>ICredentialTokenIssuer</c>), so the window in which one sits in the database is worth
///     keeping short rather than unbounded.
///
/// <para>
/// A retention period that nothing deletes against is a policy document, not a control — which
/// is the general form of Onyx A38, where export files accumulated in the deployment directory
/// forever because the cleanup was described and never written.
/// </para>
///
/// <para>
/// Every pass goes through <c>JobRunner</c>, so a <c>JobRun</c> row records the attempt, the
/// counts and any failure, and nothing can escape to take the host down. Onyx A21 ran two jobs
/// in one process with no error boundary, so either faulting killed both, with nothing to
/// restart them and a status panel that read <c>static</c> fields written in a different
/// process (A7).
/// </para>
/// </summary>
internal sealed class CustomersMaintenanceJob(
    JobRunner<CustomersDbContext> runner,
    CustomersOptions options,
    TimeProvider timeProvider,
    ILogger<CustomersMaintenanceJob> logger) : BackgroundService
{
    public const string JobName = "customers.maintenance";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer on a fixed cadence, not Task.Delay from process start. Onyx A20's
        // "nightly" report was await Task.Delay(TimeSpan.FromHours(24)) measured from startup,
        // so it drifted later every day and a restart reset the phase.
        using var timer = new PeriodicTimer(options.MaintenanceInterval, timeProvider);

        logger.LogInformation(
            "Customers maintenance started, running every {Interval}. Auth attempts are kept for "
            + "{Retention}.",
            options.MaintenanceInterval,
            options.AuthAttemptRetention);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, SweepAsync, stoppingToken);
        }
    }

    private async Task<JobResult> SweepAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<CustomersDbContext>();
        var nowUtc = timeProvider.GetUtcNow();

        // ExecuteDeleteAsync: one DELETE statement per sweep, no entities materialised. Onyx
        // A19's nightly report loaded every order in the system into memory to compute two
        // numbers; the same shape here would load every attempt row in order to delete it.
        var tokensDeleted = await context.PasswordResetTokens
            .Where(token => token.ExpiresAtUtc <= nowUtc || token.RedeemedAtUtc != null)
            .ExecuteDeleteAsync(cancellationToken);

        tokensDeleted += await context.EmailVerificationTokens
            .Where(token => token.ExpiresAtUtc <= nowUtc || token.RedeemedAtUtc != null)
            .ExecuteDeleteAsync(cancellationToken);

        var attemptsBefore = nowUtc - options.AuthAttemptRetention;

        var attemptsDeleted = await context.AuthAttempts
            .Where(attempt => attempt.OccurredAtUtc < attemptsBefore)
            .ExecuteDeleteAsync(cancellationToken);

        // Published messages have done their job. Dead-lettered ones have stopped being retried
        // and have had their failure recorded in the row's LastError and in the dispatcher's
        // logs, so the payload — which may hold a token — does not need to stay either.
        var messagesBefore = nowUtc - options.OutboxRetention;

        var messagesDeleted = await context.OutboxMessages
            .Where(message => message.ProcessedAtUtc != null && message.ProcessedAtUtc < messagesBefore)
            .ExecuteDeleteAsync(cancellationToken);

        if (tokensDeleted + attemptsDeleted + messagesDeleted > 0)
        {
            logger.LogInformation(
                "Customers maintenance deleted {Tokens} spent or expired tokens, {Attempts} "
                + "auth attempts past retention, and {Messages} processed outbox messages.",
                tokensDeleted,
                attemptsDeleted,
                messagesDeleted);
        }

        return new JobResult(tokensDeleted + attemptsDeleted + messagesDeleted);
    }
}
