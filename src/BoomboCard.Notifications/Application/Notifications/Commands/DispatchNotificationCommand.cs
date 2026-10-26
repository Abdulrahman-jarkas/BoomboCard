using BoomboCard.Notifications.Contracts;
using BoomboCard.Notifications.Data;
using BoomboCard.Notifications.Data.Repositories;
using BoomboCard.Notifications.Domain.NotificationAggregate;
using BoomboCard.Notifications.Domain.Sending;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Notifications.Application.Notifications.Commands;

/// <summary>
/// Sends one queued notification and records what happened.
///
/// <para>
/// <b>Deliberately NOT an <see cref="INotificationsRequest"/>.</b> Every marked command in this
/// system gets a transaction from its module's pipeline behaviour, and this one must not have one,
/// because it talks to a mail provider. Onyx <b>A12</b> held a transaction across up to three
/// gateway calls at a 100-second timeout each, which on a single-writer database blocked every
/// write in the system; a hanging SMTP conversation is, if anything, a better way to achieve the
/// same outage.
/// </para>
///
/// <para>
/// <c>ModuleWiringTests</c> asserts that every <c>ITransactionalRequest</c> carries exactly one
/// module marker, so the absence here is a visible decision rather than an oversight that silently
/// loses atomicity. The comment explains it; the test is what stops someone "fixing" it.
/// </para>
///
/// <para>
/// <b>Four steps, and the first one is the interesting one:</b>
/// </para>
///
/// <list type="number">
/// <item><b>Claim</b> it — <c>Queued</c> to <c>Sending</c> — in its own committed transaction.</item>
/// <item><b>Send</b>, outside every transaction.</item>
/// <item><b>Record</b> the outcome in a second short transaction.</item>
/// <item><b>Return</b> the resulting state, rather than re-reading it.</item>
/// </list>
///
/// <para>
/// Step 1 exists so that two dispatcher passes — or two instances — cannot send the same message
/// twice. A claim committed before the provider is called means the window for a duplicate is the
/// gap between claim and crash, which the stale-claim sweep then resolves; without it the window
/// is every poll interval, forever.
/// </para>
///
/// <para>
/// Step 4 is <b>BUGS-AND-LESSONS §14</b>, learned the hard way one phase ago. The Phase 5 smoke
/// run caught a payment submit returning <c>status: "Pending"</c> for a payment that was
/// <c>Captured</c>: the orchestrator re-read the aggregate through a context that had been
/// tracking it since before the provider call, and EF's identity map returned the stale instance.
/// The command that changes the state returns the state. This module was written with that
/// already known, which is the only reason it does not have the same bug.
/// </para>
/// </summary>
public sealed record DispatchNotificationCommand(Guid NotificationId)
    : IRequest<ErrorOr<DispatchedNotification>>;

public sealed record DispatchedNotification(
    Guid NotificationId,
    string Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAtUtc,
    bool Sent);

internal sealed class DispatchNotificationCommandHandler(
    INotificationSender notificationSender,
    ICommandDispatcher dispatcher,
    ILogger<DispatchNotificationCommandHandler> logger)
    : IRequestHandler<DispatchNotificationCommand, ErrorOr<DispatchedNotification>>
{
    public async Task<ErrorOr<DispatchedNotification>> Handle(
        DispatchNotificationCommand command,
        CancellationToken cancellationToken)
    {
        // ---- step 1: claim it, committed, before anything leaves the process.
        var claimed = await dispatcher.SendAsync(
            new ClaimNotificationCommand(command.NotificationId), cancellationToken);

        if (claimed.IsError)
        {
            // Somebody else got there first, or it is no longer queued. Not an error worth
            // raising: a dispatcher losing a race is the race resolving correctly.
            return claimed.Errors;
        }

        var envelope = claimed.Value;

        // ---- step 2: the send, OUTSIDE every transaction.
        SendResult result;

        try
        {
            result = await notificationSender.SendAsync(envelope, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The interface promises a SendResult and never throws for a provider outcome, so
            // reaching here means a bug in the adapter rather than a bounced email. Converted
            // to a transport failure so the notification is retried instead of being lost —
            // and logged as an error, because an adapter that throws is a defect.
            //
            // Onyx A16 is the opposite: `catch { }` in the retry loop and a Console.WriteLine
            // inside the client, so every trace of a failure was destroyed.
            logger.LogError(
                exception,
                "The {Sender} sender threw while sending notification {NotificationId}. Its "
                + "contract says it should not. Treating this as a transport failure so the "
                + "message is retried.",
                notificationSender.Name,
                command.NotificationId);

            result = new SendResult(
                DeliveryOutcome.TransportFailure,
                null,
                $"{exception.GetType().Name}: {exception.Message}",
                0,
                Guid.NewGuid().ToString("N"),
                notificationSender.Name);
        }

        LogOutcome(envelope, result);

        // ---- step 3: record it, in its own short transaction.
        //
        // A separate dispatched command, so a failure recording the outcome is retried WITHOUT
        // re-sending the message. The expensive, non-idempotent half and the cheap, retryable
        // half are on opposite sides of the boundary — the same split as a payment submission.
        var recorded = await dispatcher.SendAsync(
            new RecordDeliveryOutcomeCommand(command.NotificationId, result), cancellationToken);

        if (recorded.IsError)
        {
            // The message may well have gone out and we failed to write it down. The stale-claim
            // sweep will return it to the queue and it will be sent again — a duplicate email,
            // which is the cheap failure. The expensive one would be losing it.
            logger.LogError(
                "Notification {NotificationId} was {Outcome} by {Sender} and the outcome could "
                + "not be recorded: {Error}. The stale-claim sweep will requeue it, so a "
                + "duplicate is possible.",
                command.NotificationId,
                result.Outcome,
                result.Sender,
                recorded.FirstError.Description);

            return recorded.Errors;
        }

        // ---- step 4: the state, FROM the command that changed it. BUGS-AND-LESSONS §14.
        return recorded.Value;
    }

    private void LogOutcome(NotificationEnvelope envelope, SendResult result)
    {
        switch (result.Outcome)
        {
            case DeliveryOutcome.Accepted:
                logger.LogInformation(
                    "{Sender} accepted a {Kind} notification for {NotificationId} in "
                    + "{LatencyMs}ms. Provider id {ProviderMessageId}, correlation "
                    + "{CorrelationId}.",
                    result.Sender,
                    envelope.Kind,
                    envelope.NotificationId,
                    result.LatencyMs,
                    result.ProviderMessageId ?? "none",
                    result.CorrelationId);
                break;

            case DeliveryOutcome.Rejected:
                // A warning rather than an error: a bad address is a data problem, not an
                // outage. It is still the case somebody has to act on, which is why the
                // dead-letter event marks it as definitive.
                logger.LogWarning(
                    "{Sender} REJECTED a {Kind} notification for {NotificationId}: {Detail}. "
                    + "Not retried — the answer will not change. Correlation {CorrelationId}.",
                    result.Sender,
                    envelope.Kind,
                    envelope.NotificationId,
                    result.Detail,
                    result.CorrelationId);
                break;

            default:
                logger.LogWarning(
                    "{Sender} could not send a {Kind} notification for {NotificationId} after "
                    + "{LatencyMs}ms: {Detail}. It will be retried under backoff. Correlation "
                    + "{CorrelationId}.",
                    result.Sender,
                    envelope.Kind,
                    envelope.NotificationId,
                    result.LatencyMs,
                    result.Detail,
                    result.CorrelationId);
                break;
        }
    }
}

/// <summary>
/// Moves a notification to <c>Sending</c> and returns what to send.
///
/// <para>
/// Marked, so it gets its own transaction — and the claim is <b>committed before the provider is
/// called</b>, which is what makes two dispatcher passes unable to send the same message twice.
/// </para>
///
/// <para>
/// It returns the envelope rather than having the caller re-read the aggregate, for the same
/// reason step 4 does: a handler that reads back through its own context gets whatever its
/// identity map is holding (§14). Here it also means the body — which may contain a token — is
/// handed straight to the sender without a second query touching it.
/// </para>
/// </summary>
public sealed record ClaimNotificationCommand(Guid NotificationId)
    : IRequest<ErrorOr<NotificationEnvelope>>, INotificationsRequest;

internal sealed class ClaimNotificationCommandHandler(
    INotificationRepository notifications,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<ClaimNotificationCommand, ErrorOr<NotificationEnvelope>>
{
    public async Task<ErrorOr<NotificationEnvelope>> Handle(
        ClaimNotificationCommand command,
        CancellationToken cancellationToken)
    {
        var notification = await notifications.GetAsync(command.NotificationId, cancellationToken);

        if (notification is null)
        {
            return NotificationErrors.NotFound(command.NotificationId);
        }

        var claimed = notification.BeginSending(timeProvider.GetUtcNow());

        if (claimed.IsError)
        {
            return claimed.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new NotificationEnvelope(
            notification.Id,
            notification.Kind,
            notification.Recipient,
            notification.Subject,
            notification.Body,
            notification.ContainsSecret);
    }
}

/// <summary>
/// Applies a sender's answer. <b>The only place a notification's status moves after the claim.</b>
///
/// <para>
/// Three outcomes, three transitions, and no default that drifts towards success. Onyx <b>M1</b>
/// is the same shape in the payment path — a retry loop whose result was never inspected, so
/// every outcome produced the one branch that existed.
/// </para>
/// </summary>
public sealed record RecordDeliveryOutcomeCommand(Guid NotificationId, SendResult Result)
    : IRequest<ErrorOr<DispatchedNotification>>, INotificationsRequest;

internal sealed class RecordDeliveryOutcomeCommandHandler(
    INotificationRepository notifications,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    TimeProvider timeProvider,
    ILogger<RecordDeliveryOutcomeCommandHandler> logger)
    : IRequestHandler<RecordDeliveryOutcomeCommand, ErrorOr<DispatchedNotification>>
{
    public async Task<ErrorOr<DispatchedNotification>> Handle(
        RecordDeliveryOutcomeCommand command,
        CancellationToken cancellationToken)
    {
        var notification = await notifications.GetAsync(command.NotificationId, cancellationToken);

        if (notification is null)
        {
            return NotificationErrors.NotFound(command.NotificationId);
        }

        var nowUtc = timeProvider.GetUtcNow();
        var result = command.Result;

        // The attempt row FIRST, and unconditionally — whatever the outcome, and whether or not
        // the transition below is accepted. A8's second sentence is "and nothing records that";
        // this line is the answer to it, and it is deliberately not conditional on anything.
        notification.RecordAttempt(
            result.Outcome,
            result.Sender,
            result.ProviderMessageId,
            result.CorrelationId,
            result.Detail,
            result.LatencyMs,
            nowUtc);

        var applied = result.Outcome switch
        {
            DeliveryOutcome.Accepted => notification.MarkSent(nowUtc),

            // Definitive. Terminal, and it does not consume a retry — there is nothing to retry.
            DeliveryOutcome.Rejected => notification.Reject(result.Detail, nowUtc),

            // Transient. Back to Queued with a backoff, or dead-lettered if the budget is spent.
            // The jitter is supplied here rather than generated in the aggregate, so the
            // backoff schedule stays deterministic and unit-testable.
            DeliveryOutcome.TransportFailure => notification.Fail(
                result.Detail, nowUtc, Random.Shared.Next(0, 30)),

            _ => Error.Unexpected(
                "Notification.UnknownOutcome",
                $"Outcome '{result.Outcome}' has no defined handling, so it is not treated as "
                + "anything. Absence of a positive answer is never success.")
        };

        if (applied.IsError)
        {
            // The transition was refused — a result arriving for a notification that is already
            // terminal, say. The attempt row still stands as evidence it happened.
            logger.LogWarning(
                "Notification {NotificationId} received a {Outcome} its state machine refused: "
                + "{Error}. The attempt is recorded.",
                command.NotificationId,
                result.Outcome,
                applied.FirstError.Description);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            return applied.Errors;
        }

        if (notification.Status.IsDeadLettered)
        {
            // Worth its own line at error level. This is the exact failure Onyx had no way to
            // report: a message that will never arrive, for a customer who may be locked out.
            logger.LogError(
                "Notification {NotificationId} ({Kind} for {Reference}) has been DEAD-LETTERED "
                + "after {Attempts} attempts: {Outcome}. It will not be delivered.",
                notification.Id,
                notification.Kind,
                notification.Reference,
                notification.AttemptCount,
                notification.Outcome);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // The state, read off the aggregate this handler just mutated. The caller must not
        // re-read it — BUGS-AND-LESSONS §14.
        return new DispatchedNotification(
            notification.Id,
            notification.Status.ToString(),
            notification.AttemptCount,
            notification.NextAttemptAtUtc,
            notification.Status.IsSent);
    }
}
