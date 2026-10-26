using BoomboCard.Notifications.Domain.NotificationAggregate.Events;
using BoomboCard.Notifications.Domain.Templates;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Notifications.Domain.NotificationAggregate;

/// <summary>
/// One message this system owes one person, and everything that happened trying to deliver it.
///
/// Invariants it holds:
///  - One notification per <c>(Kind, Reference)</c>, ever. A redelivered event sends nothing new.
///  - The text is frozen at enqueue, so a retry is byte-identical to the first attempt.
///  - Every delivery attempt leaves a row.
///  - A rejection is terminal; a transport failure is retried under backoff; the budget is finite.
///  - A notification that holds a secret stops holding it once it is delivered or dead.
///
/// <para>
/// <b>Onyx A8 is two sentences, and this aggregate exists for the second one.</b>
/// </para>
///
/// <code>
/// _ = Task.Run(() => SendConfirmationEmail(customer.Email, order.Id));   // OrdersController
/// _ = Task.Run(() => NotifyPaymentConfirmed(order.Id, confirmationCode)); // PaymentsController
/// </code>
///
/// <para>
/// Both target bodies were <c>throw new NotImplementedException(...)</c>. Discarded with
/// <c>_ =</c>, so the exception was never observed, never logged, never surfaced. The register's
/// verdict: <i>"No customer has ever received any communication from this system, and nothing
/// records that. There is no retry, no dead-letter, no delivery record."</i>
/// </para>
///
/// <para>
/// The missing implementation is the obvious defect and the cheap one. The missing <i>record</i>
/// is why it survived to be written down in an audit: a system with a notifications table showing
/// ten thousand rows and zero deliveries is a system somebody fixes in an afternoon.
/// </para>
///
/// <para>
/// <b>Why this is a persisted aggregate and not just an outbox row.</b> The outbox already moves
/// events after commit, and the roadmap called this "the outbox shape, because the problem is the
/// outbox problem" — which is right about the mechanics and incomplete about the model. The outbox
/// is per-module internal plumbing whose rows are transient and whose payload is opaque. A
/// notification is a business fact with a recipient, a legible body, an audit trail, a retention
/// rule and an administrative resend. So it borrows the shape — claim, backoff, dead-letter — and
/// is its own entity.
/// </para>
/// </summary>
public sealed class NotificationRequest : AggregateRoot
{
    /// <summary>
    /// How many times a <b>transient</b> failure is retried before dead-lettering. A rejection
    /// never consumes an attempt, for the same reason a payment decline does not: there is
    /// nothing to retry.
    ///
    /// <para>
    /// Five, with exponential backoff, which spans roughly eight hours — long enough to ride out
    /// a provider outage and short enough that a dead letter still means something on the day it
    /// happens.
    /// </para>
    /// </summary>
    public const int MaxAttempts = 5;

    public const int MaxSubjectLength = 300;

    public const int MaxBodyLength = 8000;

    public const int MaxReferenceLength = 200;

    private readonly List<DeliveryAttempt> _attempts = [];

    public NotificationKind Kind { get; private init; }

    public NotificationChannel Channel { get; private init; }

    /// <summary>
    /// What this notification is about: an order id, a payment id, a customer id plus a
    /// discriminator. <b>Half of the idempotency key.</b>
    /// </summary>
    public string Reference { get; private init; } = string.Empty;

    /// <summary>
    /// Who it is for, when there is a customer. Null for a notification to an address with no
    /// account behind it — which email verification is, before the account is confirmed.
    /// </summary>
    public Guid? CustomerId { get; private init; }

    public NotificationAddress Recipient { get; private init; } = null!;

    public string Subject { get; private set; } = string.Empty;

    /// <summary>
    /// The frozen text. See <see cref="NotificationTemplate"/> for why it is rendered at enqueue
    /// rather than at send.
    ///
    /// <para>
    /// Cleared by <see cref="ForgetBody"/> once the notification is finished with, when it holds
    /// a secret. Until then it has to exist, because a retry has to send the same message.
    /// </para>
    /// </summary>
    public string Body { get; private set; } = string.Empty;

    /// <summary>
    /// Whether <see cref="Body"/> contains a plaintext token.
    ///
    /// <para>
    /// A property of the kind, carried on the row so the retention job does not have to re-derive
    /// it — and so a new kind that renders a secret is handled correctly by machinery written
    /// before it existed.
    /// </para>
    /// </summary>
    public bool ContainsSecret { get; private init; }

    public NotificationStatus Status { get; private set; } = NotificationStatus.Queued;

    /// <summary>How many transient failures have been spent.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>
    /// When the dispatcher may next try. The outbox's shape: a row is invisible to the dispatcher
    /// until its backoff has elapsed, which is what turns "retry" into "retry politely".
    /// </summary>
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public DateTimeOffset QueuedAtUtc { get; private init; }

    /// <summary>When a dispatcher pass claimed it. The stale-claim sweep's predicate.</summary>
    public DateTimeOffset? ClaimedAtUtc { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    public DateTimeOffset? FinishedAtUtc { get; private set; }

    /// <summary>Why it was suppressed or dead-lettered, when it was.</summary>
    public string? Outcome { get; private set; }

    public IReadOnlyCollection<DeliveryAttempt> Attempts => _attempts.AsReadOnly();

    private NotificationRequest() { }

    /// <summary>
    /// Queues a notification, with its text already rendered.
    ///
    /// <para>
    /// Called inside the transaction that consumed the integration event, so a notification exists
    /// if and only if the thing it describes committed. That is <b>A9</b>: Onyx dispatched its
    /// payment notification before the payment row was added to the context, before
    /// <c>SaveChanges</c> and before <c>Commit</c>, so a rolled-back transaction left a customer
    /// holding a confirmation for something that never happened.
    /// </para>
    ///
    /// <para>
    /// <paramref name="nowUtc"/> is also the first <see cref="NextAttemptAtUtc"/>, so a fresh
    /// notification is immediately dispatchable. A null there would make it invisible to the
    /// dispatcher forever — which is a mistake this project has already made once, in the Phase 4
    /// verification script, and worth not repeating in the model.
    /// </para>
    /// </summary>
    public static ErrorOr<NotificationRequest> Queue(
        NotificationKind kind,
        NotificationChannel channel,
        string? reference,
        Guid? customerId,
        NotificationAddress recipient,
        RenderedNotification rendered,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return NotificationErrors.ReferenceRequired;
        }

        if (reference.Length > MaxReferenceLength)
        {
            return NotificationErrors.ReferenceTooLong;
        }

        if (string.IsNullOrWhiteSpace(rendered.Subject) || string.IsNullOrWhiteSpace(rendered.Body))
        {
            return NotificationErrors.NothingToSend;
        }

        if (rendered.Subject.Length > MaxSubjectLength || rendered.Body.Length > MaxBodyLength)
        {
            return NotificationErrors.RenderedTextTooLong;
        }

        var notification = new NotificationRequest
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Channel = channel,
            Reference = reference.Trim(),
            CustomerId = customerId,
            Recipient = recipient,
            Subject = rendered.Subject,
            Body = rendered.Body,
            ContainsSecret = rendered.ContainsSecret,
            Status = NotificationStatus.Queued,
            QueuedAtUtc = nowUtc,
            NextAttemptAtUtc = nowUtc
        };

        notification.RaiseDomainEvent(new NotificationQueuedEvent(
            notification.Id, kind, notification.Reference, customerId, nowUtc));

        return notification;
    }

    /// <summary>Whether the dispatcher may pick this up at <paramref name="nowUtc"/>.</summary>
    public bool IsDueAt(DateTimeOffset nowUtc) =>
        Status.IsDispatchable
            && NextAttemptAtUtc is not null
            && NextAttemptAtUtc <= nowUtc;

    /// <summary>
    /// A dispatcher pass takes ownership.
    ///
    /// <para>
    /// The claim is committed <b>before</b> the provider is called, and the provider is called
    /// outside every transaction — the same three-step shape as a payment, and for the same
    /// reason (<b>A12</b>). A claim with nobody behind it is recoverable by
    /// <see cref="ReleaseStaleClaim"/>; a transaction held across an SMTP conversation is not
    /// recoverable by anything.
    /// </para>
    /// </summary>
    public ErrorOr<Success> BeginSending(DateTimeOffset nowUtc)
    {
        var status = Status.BeginSending();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        ClaimedAtUtc = nowUtc;

        return Result.Success;
    }

    /// <summary>The provider accepted it.</summary>
    public ErrorOr<Success> MarkSent(DateTimeOffset nowUtc)
    {
        var status = Status.MarkSent();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        SentAtUtc = nowUtc;
        FinishedAtUtc = nowUtc;
        NextAttemptAtUtc = null;
        ClaimedAtUtc = null;
        Outcome = null;

        RaiseDomainEvent(new NotificationSentEvent(Id, Kind, Reference, CustomerId, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// The provider refused it and will refuse it again. Terminal, and it does not consume an
    /// attempt — there is nothing to retry.
    /// </summary>
    public ErrorOr<Success> Reject(string? reason, DateTimeOffset nowUtc)
    {
        var status = Status.Reject();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        FinishedAtUtc = nowUtc;
        NextAttemptAtUtc = null;
        ClaimedAtUtc = null;
        Outcome = Trim(reason) ?? "rejected by the provider";

        RaiseDomainEvent(new NotificationUndeliverableEvent(
            Id, Kind, Reference, CustomerId, Recipient.Value, Outcome, Definitive: true, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// A transient failure. Back to <c>Queued</c> with a backoff, or dead-lettered if the budget
    /// is spent.
    ///
    /// <para>
    /// <b>Exponential with jitter</b>, computed here rather than in the job, so the backoff is a
    /// property of the aggregate and is unit-testable without a clock or a scheduler. Attempt 1
    /// waits ~1 minute, attempt 5 ~16 — roughly eight hours in total, which rides out a provider
    /// outage without making a dead letter stale news.
    /// </para>
    ///
    /// <para>
    /// The jitter matters more than it looks: a provider outage queues thousands of
    /// notifications, and without jitter every one of them retries in the same second and
    /// re-creates the outage the moment it clears.
    /// </para>
    /// </summary>
    public ErrorOr<Success> Fail(string? reason, DateTimeOffset nowUtc, int jitterSeconds = 0)
    {
        if (jitterSeconds < 0)
        {
            return NotificationErrors.NegativeJitter;
        }

        var attempts = AttemptCount + 1;
        var budgetSpent = attempts >= MaxAttempts;

        var status = Status.Fail(budgetSpent);

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        AttemptCount = attempts;
        ClaimedAtUtc = null;
        Outcome = Trim(reason);

        if (budgetSpent)
        {
            FinishedAtUtc = nowUtc;
            NextAttemptAtUtc = null;

            // THE EVENT THAT MAKES THIS DIFFERENT FROM ONYX.
            //
            // A8: "notifications fail silently and forever". This one fails loudly and finitely —
            // the dead letter is published, so it becomes something another module can alert on
            // rather than a row nobody queries.
            RaiseDomainEvent(new NotificationUndeliverableEvent(
                Id,
                Kind,
                Reference,
                CustomerId,
                Recipient.Value,
                Outcome ?? "delivery failed",
                Definitive: false,
                nowUtc));
        }
        else
        {
            NextAttemptAtUtc = nowUtc + BackoffFor(attempts, jitterSeconds);
        }

        return Result.Success;
    }

    /// <summary>
    /// How long to wait before attempt <paramref name="attempt"/> + 1.
    ///
    /// <para>
    /// Public and static so the schedule can be asserted directly: a backoff that quietly
    /// collapsed to zero would turn a provider outage into a denial of service against the
    /// provider, and that is not visible from reading the job.
    /// </para>
    /// </summary>
    public static TimeSpan BackoffFor(int attempt, int jitterSeconds = 0) =>
        TimeSpan.FromMinutes(Math.Pow(2, Math.Max(0, attempt - 1)))
            + TimeSpan.FromSeconds(jitterSeconds);

    /// <summary>
    /// Returns a claim nobody completed, so the dispatcher can try again.
    ///
    /// <para>
    /// A process killed between claiming and sending leaves a row in <c>Sending</c>. Without this
    /// it stays there forever, which is Onyx's "silently and forever" reached by a different road
    /// — and the reason <c>JobRunner</c> writes a run row before the work starts is the same
    /// instinct: a thing stuck mid-flight should be visible, not absent.
    /// </para>
    ///
    /// <para>
    /// It does <b>not</b> consume an attempt. We do not know whether the provider was called, so
    /// charging the budget for it would let a crash loop burn through the retries without a single
    /// send being attempted.
    /// </para>
    /// </summary>
    public ErrorOr<Success> ReleaseStaleClaim(DateTimeOffset nowUtc)
    {
        var status = Status.ReleaseStaleClaim();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        ClaimedAtUtc = null;
        NextAttemptAtUtc = nowUtc;

        return Result.Success;
    }

    /// <summary>
    /// Deliberately not sent.
    ///
    /// <para>
    /// Recorded rather than dropped, because "we chose not to" and "we failed to" are different
    /// facts and only one of them is a defect. The case that reaches this today is a recipient who
    /// cannot be resolved — an anonymised customer, say.
    /// </para>
    /// </summary>
    public ErrorOr<Success> Suppress(string reason, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return NotificationErrors.SuppressionReasonRequired;
        }

        var status = Status.Suppress();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        FinishedAtUtc = nowUtc;
        NextAttemptAtUtc = null;
        ClaimedAtUtc = null;
        Outcome = reason.Trim();

        return Result.Success;
    }

    /// <summary>
    /// Appends an attempt row. Called for every provider interaction, whatever the outcome.
    ///
    /// <para>
    /// Separate from the status transitions on purpose — an attempt is recorded even when the
    /// transition that follows is refused, because the history is only useful if it is complete.
    /// The same arrangement as <c>Payment.RecordAttempt</c>.
    /// </para>
    /// </summary>
    public DeliveryAttempt RecordAttempt(
        DeliveryOutcome outcome,
        string sender,
        string? providerMessageId,
        string correlationId,
        string? detail,
        int latencyMs,
        DateTimeOffset nowUtc)
    {
        var attempt = DeliveryAttempt.Record(
            Id,
            _attempts.Count + 1,
            outcome,
            sender,
            providerMessageId,
            correlationId,
            detail,
            latencyMs,
            nowUtc);

        _attempts.Add(attempt);

        return attempt;
    }

    /// <summary>
    /// Discards the body of a finished notification that was holding a secret.
    ///
    /// <para>
    /// <b>Why this exists.</b> A verification or reset body contains a plaintext token, because
    /// the email is the only thing that can deliver one — the database stores a hash, so after
    /// the message is built nobody, this system included, can recover it from anywhere else. That
    /// makes the notifications table a credential store for as long as the row holds the text.
    /// </para>
    ///
    /// <para>
    /// The token itself already expires, so this is defence in depth rather than the primary
    /// control. But a table full of live reset links is exactly the thing that gets copied into a
    /// reporting warehouse and read by people who have no business holding credentials, and Onyx
    /// <b>S14</b> stored its reset tokens in plaintext with no expiry at all.
    /// </para>
    ///
    /// <para>
    /// Only once the notification is terminal: a queued one still needs its text to be sent, and
    /// a retry has to send the same message.
    /// </para>
    /// </summary>
    public bool ForgetBody()
    {
        if (!ContainsSecret || !Status.IsTerminal || Body.Length == 0)
        {
            return false;
        }

        // The body only. The subject stays, because no template puts a token in one and "Reset
        // your password" is what makes the audit trail readable afterwards — a row with neither
        // subject nor body records that something was sent and nothing about what.
        Body = string.Empty;

        return true;
    }

    /// <summary>
    /// Whether this notification still holds a secret it should not.
    ///
    /// <para>
    /// The invariant <c>notifications.integrity-check</c> asserts — and, like every integrity
    /// check in this project, it <b>reports and never corrects</b>. The retention job does the
    /// forgetting; a check that silently cleared bodies would hide a retention job that had
    /// stopped running, which is Onyx <b>D5</b>'s shape.
    /// </para>
    /// </summary>
    public bool HoldsAStaleSecret(DateTimeOffset nowUtc, TimeSpan grace) =>
        ContainsSecret
            && Body.Length > 0
            && Status.IsTerminal
            && FinishedAtUtc is not null
            && FinishedAtUtc + grace <= nowUtc;

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class NotificationErrors
{
    public static Error NotFound(Guid notificationId) =>
        Error.NotFound("Notification.NotFound", $"No notification with id '{notificationId}'.");

    public static Error ReferenceRequired =>
        Error.Validation(
            "Notification.ReferenceRequired",
            "A notification must name what it is about, because the reference is half of the "
            + "idempotency key that stops a redelivered event sending a second message.");

    public static Error ReferenceTooLong =>
        Error.Validation(
            "Notification.ReferenceTooLong",
            $"A reference is at most {NotificationRequest.MaxReferenceLength} characters.");

    public static Error NothingToSend =>
        Error.Validation(
            "Notification.NothingToSend",
            "A notification needs a subject and a body. An empty message is a template bug, and "
            + "sending it would spend a customer's attention on nothing.");

    public static Error RenderedTextTooLong =>
        Error.Validation(
            "Notification.RenderedTextTooLong",
            $"A subject is at most {NotificationRequest.MaxSubjectLength} characters and a body "
            + $"at most {NotificationRequest.MaxBodyLength}. Truncating silently at the database "
            + "would send a half-finished message.");

    public static Error SuppressionReasonRequired =>
        Error.Validation(
            "Notification.SuppressionReasonRequired",
            "Suppressing a notification must say why, or it is indistinguishable from losing it.");

    public static Error NegativeJitter =>
        Error.Validation("Notification.NegativeJitter", "Jitter cannot be negative.");

    public static Error AlreadyQueued(NotificationKind kind, string reference) =>
        Error.Conflict(
            "Notification.AlreadyQueued",
            $"A '{kind}' notification already exists for '{reference}'. One per thing, ever — a "
            + "redelivered event must not send a second message.");

    public static Error RecipientUnresolved(Guid customerId) =>
        Error.NotFound(
            "Notification.RecipientUnresolved",
            $"No address could be resolved for customer '{customerId}'.");
}
