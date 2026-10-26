using BoomboCard.Payments.Application.Payments.Commands;
using BoomboCard.Payments.Data;
using BoomboCard.Payments.Data.Repositories;
using BoomboCard.Payments.Domain.Gateway;
using BoomboCard.Payments.Domain.PaymentAggregate;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Payments.Application.Reconciliation;

public sealed class PaymentsOptions
{
    /// <summary>How often payments with an unknown outcome are chased with the provider.</summary>
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long to wait before asking the provider about an unanswered request.
    ///
    /// <para>
    /// Long enough that a provider still processing the original request has finished — asking
    /// too early gets "unknown" back and achieves nothing — and short enough that a customer is
    /// not left in doubt about whether they have been charged. One minute against a typical
    /// provider timeout of thirty seconds.
    /// </para>
    /// </summary>
    public TimeSpan ReconciliationGracePeriod { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the refund arithmetic is re-derived from the refund rows.</summary>
    public TimeSpan IntegrityCheckInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a processed outbox message is kept.</summary>
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long provider webhook records are kept.
    ///
    /// <para>
    /// Much longer than the outbox, deliberately: a chargeback can be raised months after the
    /// charge, and the thing that settles it is the payload the provider actually sent. A year is
    /// the shortest window that covers the usual dispute deadlines.
    /// </para>
    /// </summary>
    public TimeSpan WebhookRetention { get; init; } = TimeSpan.FromDays(365);
}

/// <summary>
/// Asks the provider what happened to requests we never got an answer for.
///
/// <para>
/// <b>This job is the answer to the single most dangerous case in payments</b>, and the one Onyx
/// got exactly backwards. From <b>M5</b>:
/// </para>
///
/// <blockquote>
/// The dangerous case is a timeout where the charge <i>succeeded</i> provider-side but the
/// response was lost: the system treats it as a failure and charges again.
/// </blockquote>
///
/// <para>
/// There are only three things you can do with an unanswered charge request. You can assume it
/// failed and charge again, which is how a customer gets billed twice. You can assume it
/// succeeded, which is <b>M1</b> — a payment row reading "Paid" for money that never moved. Or
/// you can <b>go and ask</b>, which requires having sent something the provider can recognise the
/// request by. That is what the idempotency key is for, and it is why the key is generated once,
/// committed before the first attempt, and sent unchanged on every one.
/// </para>
///
/// <para>
/// So this job is not a nicety bolted on at the end: <c>IdempotencyKey</c>,
/// <c>AttemptOutcome.Indeterminate</c>, <c>IPaymentGateway.EnquireAsync</c> and
/// <c>Payment.IndeterminateSinceUtc</c> all exist to make it possible, and without it none of
/// them would have a purpose.
/// </para>
///
/// <para>
/// It <b>corrects</b> rather than merely reports, which is the opposite of
/// <c>sales.integrity-check</c> and the stock equivalent — and the difference is worth stating,
/// because this project is otherwise strict about jobs that fix things. A reconciliation job does
/// not compute a figure and impose it; it <i>retrieves the answer from the system of record</i>,
/// which for "did this card get charged?" is the provider and never us. Onyx <b>D5</b> is a job
/// inventing a value it had no authority over. This job has no opinion at all.
/// </para>
/// </summary>
internal sealed class PaymentReconciliationJob(
    JobRunner<PaymentsDbContext> runner,
    PaymentsOptions options,
    TimeProvider timeProvider,
    ILogger<PaymentReconciliationJob> logger) : BackgroundService
{
    public const string JobName = "payments.reconciliation";

    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ReconciliationInterval, timeProvider);

        logger.LogInformation(
            "Payment reconciliation started, running every {Interval} over unanswered requests "
            + "older than {Grace}.",
            options.ReconciliationInterval,
            options.ReconciliationGracePeriod);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, ReconcileAsync, stoppingToken);
        }
    }

    private async Task<JobResult> ReconcileAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var payments = services.GetRequiredService<IPaymentRepository>();
        var gateway = services.GetRequiredService<IPaymentGateway>();
        var dispatcher = services.GetRequiredService<ICommandDispatcher>();

        var cutoff = timeProvider.GetUtcNow() - options.ReconciliationGracePeriod;

        var candidates = await payments.GetIndeterminatePaymentIdsAsync(
            cutoff, BatchSize, cancellationToken);

        var resolved = 0;
        var stillUnknown = 0;

        foreach (var paymentId in candidates)
        {
            var payment = await payments.GetAsync(paymentId, cancellationToken);

            if (payment is null)
            {
                continue;
            }

            GatewayResult result;

            try
            {
                // A read, so safe to repeat. The key is what makes the question answerable.
                result = await gateway.EnquireAsync(
                    new GatewayEnquiryRequest(payment.Id, payment.IdempotencyKey.Value),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Logged with full context and counted, never swallowed — Onyx A16's empty catch
                // blocks destroyed every trace of a payment failure.
                logger.LogError(
                    exception,
                    "Could not ask the provider about payment {Reference}. It stays unresolved "
                    + "and the next pass will try again.",
                    payment.Reference.Value);

                stillUnknown++;
                continue;
            }

            if (result.Outcome is AttemptOutcome.Indeterminate
                or AttemptOutcome.TransportFailure)
            {
                // The provider does not know either, or we could not reach it. Left alone: a
                // payment whose outcome is genuinely unknown must stay visibly unknown rather
                // than be quietly resolved in some direction.
                logger.LogWarning(
                    "The provider still cannot say what happened to payment {Reference} "
                    + "(unanswered since {Since}). Correlation {CorrelationId}.",
                    payment.Reference.Value,
                    payment.IndeterminateSinceUtc,
                    result.CorrelationId);

                stillUnknown++;
                continue;
            }

            var recorded = await dispatcher.SendAsync(
                new RecordPaymentOutcomeCommand(payment.Id, PaymentOperation.Enquire, result),
                cancellationToken);

            if (recorded.IsError)
            {
                logger.LogError(
                    "The provider answered for payment {Reference} ({Outcome}) and the answer "
                    + "could not be recorded: {Error}.",
                    payment.Reference.Value,
                    result.Outcome,
                    recorded.FirstError.Description);

                stillUnknown++;
                continue;
            }

            // Worth an explicit log line each: a charge that succeeded without us knowing is a
            // near miss, and the customer may be mid-complaint about it.
            logger.LogWarning(
                "Payment {Reference} had no answer since {Since}; the provider says {Outcome}. "
                + "Resolved from the provider's own record — the money {Verb}.",
                payment.Reference.Value,
                payment.IndeterminateSinceUtc,
                result.Outcome,
                result.Succeeded ? "HAD moved and we did not know" : "had not moved");

            resolved++;
        }

        return new JobResult(resolved, stillUnknown);
    }
}

/// <summary>
/// Asserts that the refunded total equals the sum of the refund rows, and never exceeds what was
/// captured. <b>Reports, never corrects.</b>
///
/// <para>
/// The fifth state-versus-log pairing in this codebase, after stock, credentials, promotion
/// redemptions and loyalty points. Note the deliberate contrast with
/// <c>PaymentReconciliationJob</c> above, which does correct: that one retrieves an answer from
/// the system of record, while this one would have to <i>compute</i> the right figure — and a job
/// that computes a monetary figure and writes it is Onyx <b>D5</b> with money instead of stock.
/// </para>
///
/// <para>
/// BUGS-AND-LESSONS §13 is the reason the second clause exists. Agreement between a counter and
/// its log proves they are <i>consistent</i>, not that either is <i>right</i> — so the check that
/// has teeth is the bound: a refunded total above the captured amount is money leaving that never
/// arrived.
/// </para>
/// </summary>
internal sealed class PaymentsIntegrityCheckJob(
    JobRunner<PaymentsDbContext> runner,
    PaymentsOptions options,
    TimeProvider timeProvider,
    ILogger<PaymentsIntegrityCheckJob> logger) : BackgroundService
{
    public const string JobName = "payments.integrity-check";

    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.IntegrityCheckInterval, timeProvider);

        logger.LogInformation(
            "Payments integrity check started, running every {Interval}. It reports mismatches "
            + "and corrects nothing.",
            options.IntegrityCheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await runner.RunAsync(JobName, CheckAsync, stoppingToken);
        }
    }

    private async Task<JobResult> CheckAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<PaymentsDbContext>();

        // Newest first, capped, and ORDERED because it is capped. BUGS-AND-LESSONS §8.
        var rows = await context.Payments
            .AsNoTracking()
            .OrderByDescending(payment => payment.InitiatedAtUtc)
            .Take(BatchSize)
            .Select(payment => new
            {
                payment.Id,
                Reference = payment.Reference.Value,
                Captured = payment.Amount.Amount,
                Stored = payment.RefundedAmount.Amount,
                RowSum = payment.Refunds.Sum(refund => refund.Amount.Amount),
                Status = payment.Status.Value,
                payment.SubmissionAttempts,
                AttemptCount = payment.Attempts.Count,
                HasProviderReference = payment.ProviderReference != null
            })
            .ToListAsync(cancellationToken);

        var inspected = 0;
        var mismatches = 0;

        foreach (var row in rows)
        {
            inspected++;

            if (row.Stored != row.RowSum)
            {
                mismatches++;

                logger.LogError(
                    "Payment {Reference} stores a refunded total of {Stored} but its refund rows "
                    + "sum to {RowSum}. NOT corrected — this is a defect and the row is the "
                    + "evidence.",
                    row.Reference,
                    row.Stored,
                    row.RowSum);
            }

            if (row.Stored > row.Captured)
            {
                mismatches++;

                logger.LogError(
                    "Payment {Reference} has refunded {Stored} against a captured amount of "
                    + "{Captured}. That is money leaving which never arrived. NOT corrected.",
                    row.Reference,
                    row.Stored,
                    row.Captured);
            }

            // M4, asserted continuously rather than only at the transition: a settled payment
            // without a provider reference is the state Onyx's fabricated reference hid.
            if (IsSettled(row.Status) && !row.HasProviderReference)
            {
                mismatches++;

                logger.LogError(
                    "Payment {Reference} is {Status} with NO provider reference. It cannot be "
                    + "reconciled, refunded or defended — this is Onyx M4 and it should be "
                    + "unreachable.",
                    row.Reference,
                    row.Status);
            }

            // A17, asserted the same way: a payment that reached a provider must have a history.
            if (row.SubmissionAttempts > 0 && row.AttemptCount == 0)
            {
                mismatches++;

                logger.LogError(
                    "Payment {Reference} records {Submissions} submission attempt(s) and has no "
                    + "attempt rows. The audit trail is incomplete.",
                    row.Reference,
                    row.SubmissionAttempts);
            }
        }

        if (mismatches > 0)
        {
            logger.LogError(
                "Payments integrity check found {Mismatches} problem(s) out of {Inspected} "
                + "payments checked.",
                mismatches,
                inspected);
        }

        return new JobResult(inspected, mismatches);
    }

    private static bool IsSettled(PaymentStatusValue status) =>
        status is PaymentStatusValue.Authorized
            or PaymentStatusValue.Captured
            or PaymentStatusValue.PartiallyRefunded
            or PaymentStatusValue.Refunded
            or PaymentStatusValue.ChargedBack;
}
