using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Notifications.Domain.NotificationAggregate;

/// <summary>
/// Where a notification is.
///
/// <para>
/// <b>Onyx had no representation for any of this.</b> <b>A8</b>'s register entry is the harshest
/// sentence in the whole audit: <i>"No customer has ever received any communication from this
/// system, and nothing records that."</i> Two defects compound into it —
/// <c>_ = Task.Run(() =&gt; SendConfirmationEmail(...))</c> discards the task so no exception is
/// ever observed, and the target method's body is <c>throw new NotImplementedException(...)</c>
/// so there was always an exception to not observe.
/// </para>
///
/// <para>
/// The second half of that sentence is the one this enum answers. A system that cannot send email
/// is a bug; a system that cannot <i>tell you</i> it cannot send email is a different and worse
/// bug, because the first is noticed in a day and the second is noticed by a customer.
/// </para>
/// </summary>
public enum NotificationStatusValue
{
    /// <summary>
    /// Rendered and waiting for the dispatcher. Reached inside the transaction that consumed the
    /// integration event, so a notification exists if and only if the thing it describes happened.
    /// </summary>
    Queued = 1,

    /// <summary>
    /// Claimed by a dispatcher pass. A row stuck here means a process died mid-send — visible,
    /// rather than invisible, and swept back to <c>Queued</c> by the dispatcher's own stale-claim
    /// check.
    /// </summary>
    Sending = 2,

    /// <summary>The provider accepted it. As far as this system can know, it was sent.</summary>
    Sent = 3,

    /// <summary>
    /// The provider refused it, definitively — an invalid address, a hard bounce, a blocked
    /// domain. <b>Never retried</b>, because the answer will not change.
    /// </summary>
    Rejected = 4,

    /// <summary>
    /// Every attempt failed and the budget is spent.
    ///
    /// <para>
    /// <b>This status is the whole point of the module.</b> Onyx's notifications failed silently
    /// and forever; these fail loudly and finitely. A dead-lettered notification publishes an
    /// integration event, so the failure becomes something another module can alert on rather
    /// than a row nobody queries.
    /// </para>
    /// </summary>
    DeadLettered = 5,

    /// <summary>
    /// Deliberately not sent — the recipient could not be resolved, or the address is one this
    /// system has been told to stop writing to. Recorded rather than dropped, because "we chose
    /// not to" and "we failed to" are different facts and only one of them is a defect.
    /// </summary>
    Suppressed = 6
}

/// <summary>
/// The notification state machine. Guarded transitions, each returning the next status or an
/// error naming why not.
/// </summary>
public sealed class NotificationStatus : ValueObject
{
    public NotificationStatusValue Value { get; }

    private NotificationStatus(NotificationStatusValue value) => Value = value;

    public static NotificationStatus Queued => new(NotificationStatusValue.Queued);

    public static NotificationStatus From(NotificationStatusValue value) => new(value);

    public bool IsQueued => Value is NotificationStatusValue.Queued;

    public bool IsSending => Value is NotificationStatusValue.Sending;

    public bool IsSent => Value is NotificationStatusValue.Sent;

    public bool IsDeadLettered => Value is NotificationStatusValue.DeadLettered;

    /// <summary>
    /// Finished with, one way or another. A terminal notification is never dispatched again by
    /// the job — only by an explicit administrative resend, which creates a new notification
    /// rather than reviving this one.
    /// </summary>
    public bool IsTerminal =>
        Value is NotificationStatusValue.Sent
            or NotificationStatusValue.Rejected
            or NotificationStatusValue.DeadLettered
            or NotificationStatusValue.Suppressed;

    /// <summary>Whether the dispatcher may pick this up.</summary>
    public bool IsDispatchable => Value is NotificationStatusValue.Queued;

    /// <summary>A dispatcher pass has taken ownership of it.</summary>
    public ErrorOr<NotificationStatus> BeginSending() =>
        Value is NotificationStatusValue.Queued
            ? new NotificationStatus(NotificationStatusValue.Sending)
            : NotificationStatusErrors.NotDispatchable;

    /// <summary>The provider accepted it.</summary>
    public ErrorOr<NotificationStatus> MarkSent() =>
        Value is NotificationStatusValue.Sending or NotificationStatusValue.Queued
            ? new NotificationStatus(NotificationStatusValue.Sent)
            : Value is NotificationStatusValue.Sent
                ? NotificationStatusErrors.AlreadySent
                : NotificationStatusErrors.NotSendable;

    /// <summary>
    /// The provider refused it, and will refuse it again. Terminal.
    ///
    /// <para>
    /// The counterpart of a payment decline, and treated the same way: a definitive answer is not
    /// a failure, and retrying one wastes the budget that a genuinely transient failure needs.
    /// </para>
    /// </summary>
    public ErrorOr<NotificationStatus> Reject() =>
        Value is NotificationStatusValue.Sending or NotificationStatusValue.Queued
            ? new NotificationStatus(NotificationStatusValue.Rejected)
            : NotificationStatusErrors.NotSendable;

    /// <summary>
    /// A transient failure. Back to <c>Queued</c>, for the dispatcher to try again after the
    /// backoff — unless <paramref name="budgetSpent"/>, in which case it dead-letters.
    /// </summary>
    public ErrorOr<NotificationStatus> Fail(bool budgetSpent) =>
        Value is NotificationStatusValue.Sending or NotificationStatusValue.Queued
            ? new NotificationStatus(budgetSpent
                ? NotificationStatusValue.DeadLettered
                : NotificationStatusValue.Queued)
            : NotificationStatusErrors.NotSendable;

    /// <summary>
    /// Returns a claim nobody completed. The dispatcher's stale-claim sweep: a process that died
    /// between claiming and sending leaves a row in <c>Sending</c>, and without this it would sit
    /// there forever — which is Onyx's "silently and forever" reached by a new route.
    /// </summary>
    public ErrorOr<NotificationStatus> ReleaseStaleClaim() =>
        Value is NotificationStatusValue.Sending
            ? new NotificationStatus(NotificationStatusValue.Queued)
            : NotificationStatusErrors.NotClaimed;

    /// <summary>Deliberately not sent.</summary>
    public ErrorOr<NotificationStatus> Suppress() =>
        Value is NotificationStatusValue.Queued or NotificationStatusValue.Sending
            ? new NotificationStatus(NotificationStatusValue.Suppressed)
            : NotificationStatusErrors.NotSuppressible;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString();
}

public static class NotificationStatusErrors
{
    public static Error NotDispatchable =>
        Error.Conflict(
            "Notification.NotDispatchable",
            "This notification is not queued, so it cannot be claimed for sending.");

    public static Error AlreadySent =>
        Error.Conflict("Notification.AlreadySent", "This notification has already been sent.");

    public static Error NotSendable =>
        Error.Conflict(
            "Notification.NotSendable",
            "This notification has already been answered and will not be sent again.");

    public static Error NotClaimed =>
        Error.Conflict(
            "Notification.NotClaimed", "This notification is not mid-send, so there is no claim to release.");

    public static Error NotSuppressible =>
        Error.Conflict(
            "Notification.NotSuppressible", "This notification has already been answered.");
}
