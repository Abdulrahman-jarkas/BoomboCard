using BoomboCard.Notifications.Domain.NotificationAggregate;
using BoomboCard.Notifications.Domain.NotificationAggregate.Events;
using BoomboCard.Notifications.Domain.Templates;

namespace BoomboCard.Notifications.Tests;

/// <summary>
/// The aggregate. <b>These are the tests that keep A8 closed.</b>
///
/// <para>
/// That finding is two sentences: <i>"No customer has ever received any communication from this
/// system, and nothing records that. There is no retry, no dead-letter, no delivery record."</i>
/// There is now a retry schedule, a dead-letter, and a row per attempt — and this file asserts
/// each of them.
/// </para>
/// </summary>
public class NotificationRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 9, 0, 0, TimeSpan.Zero);

    private static NotificationAddress Address(string value = "sam@example.test") =>
        NotificationAddress.Create(value, "Sam").Value;

    private static NotificationRequest Queued(
        NotificationKind kind = NotificationKind.OrderPlaced,
        string reference = "order-1",
        bool secret = false)
    {
        var queued = NotificationRequest.Queue(
            kind,
            NotificationChannel.Email,
            reference,
            Guid.NewGuid(),
            Address(),
            new RenderedNotification("Subject", "Body", secret),
            Now);

        Assert.False(queued.IsError, queued.IsError ? queued.FirstError.Description : "");

        return queued.Value;
    }

    // ------------------------------------------------------------------ queueing

    /// <summary>
    /// A fresh notification is queued and <b>immediately due</b>.
    ///
    /// <para>
    /// <c>NextAttemptAtUtc</c> being set at creation is load-bearing: the dispatcher's query
    /// requires it to be non-null, so a null there would make the notification invisible forever —
    /// which is Onyx's "silently and forever" reached by a new route. This project has already
    /// made that exact mistake once, in the Phase 4 verification script's hand-built outbox row.
    /// </para>
    /// </summary>
    [Fact]
    public void AFreshNotificationIsQueuedAndImmediatelyDue()
    {
        var notification = Queued();

        Assert.True(notification.Status.IsQueued);
        Assert.Equal(Now, notification.NextAttemptAtUtc);
        Assert.True(notification.IsDueAt(Now));
        Assert.Equal(0, notification.AttemptCount);
        Assert.Empty(notification.Attempts);
        Assert.Null(notification.SentAtUtc);
        Assert.Null(notification.ClaimedAtUtc);
    }

    [Fact]
    public void QueueingRaisesAnEvent()
    {
        var notification = Queued(NotificationKind.PaymentReceived, "payment-7");

        var queued = Assert.Single(notification.DomainEvents.OfType<NotificationQueuedEvent>());

        Assert.Equal(NotificationKind.PaymentReceived, queued.Kind);
        Assert.Equal("payment-7", queued.Reference);
    }

    /// <summary>
    /// A reference is required, because it is half of the idempotency key — and without one, a
    /// redelivered event would send a second email.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ANotificationMustNameWhatItIsAbout(string? reference)
    {
        var queued = NotificationRequest.Queue(
            NotificationKind.OrderPlaced,
            NotificationChannel.Email,
            reference,
            Guid.NewGuid(),
            Address(),
            new RenderedNotification("Subject", "Body", false),
            Now);

        Assert.True(queued.IsError);
        Assert.Equal("Notification.ReferenceRequired", queued.FirstError.Code);
    }

    /// <summary>
    /// An empty message is refused rather than queued. It is a template bug, and sending it would
    /// spend a customer's attention on nothing.
    /// </summary>
    [Theory]
    [InlineData("", "Body")]
    [InlineData("Subject", "")]
    public void AnEmptyMessageIsRefused(string subject, string body)
    {
        var queued = NotificationRequest.Queue(
            NotificationKind.OrderPlaced,
            NotificationChannel.Email,
            "order-1",
            Guid.NewGuid(),
            Address(),
            new RenderedNotification(subject, body, false),
            Now);

        Assert.True(queued.IsError);
        Assert.Equal("Notification.NothingToSend", queued.FirstError.Code);
    }

    /// <summary>
    /// Over-long text is refused by the aggregate rather than truncated by the database.
    ///
    /// <para>
    /// BUGS-AND-LESSONS §9 is this exact shape: the outbox payload column inherited a model-wide
    /// 500-character convention and was two fields away from silently failing every integration
    /// event. A silently truncated email is a half-finished message sent to a customer.
    /// </para>
    /// </summary>
    [Fact]
    public void OverLongRenderedTextIsRefusedRatherThanTruncated()
    {
        var queued = NotificationRequest.Queue(
            NotificationKind.OrderPlaced,
            NotificationChannel.Email,
            "order-1",
            Guid.NewGuid(),
            Address(),
            new RenderedNotification("Subject", new string('x', NotificationRequest.MaxBodyLength + 1), false),
            Now);

        Assert.True(queued.IsError);
        Assert.Equal("Notification.RenderedTextTooLong", queued.FirstError.Code);
    }

    // ------------------------------------------------------------------ the claim

    /// <summary>
    /// The claim is what stops two dispatcher passes sending the same message.
    ///
    /// <para>
    /// Committed before the provider is called, so the window for a duplicate is the gap between
    /// claim and crash — which the stale-claim sweep resolves. Without it the window is every poll
    /// interval, forever.
    /// </para>
    /// </summary>
    [Fact]
    public void ClaimingMakesItInvisibleToTheDispatcher()
    {
        var notification = Queued();

        Assert.False(notification.BeginSending(Now).IsError);

        Assert.True(notification.Status.IsSending);
        Assert.Equal(Now, notification.ClaimedAtUtc);
        Assert.False(notification.IsDueAt(Now.AddHours(1)));

        // And a second claim is refused, which is the race resolving correctly.
        var again = notification.BeginSending(Now);
        Assert.True(again.IsError);
        Assert.Equal("Notification.NotDispatchable", again.FirstError.Code);
    }

    /// <summary>
    /// <b>The stale-claim sweep.</b> A process killed between claiming and sending leaves a row in
    /// <c>Sending</c>; without this it stays there forever, which is Onyx's "silently and forever"
    /// by a new road.
    ///
    /// <para>
    /// It does <b>not</b> consume an attempt. We do not know whether the provider was called, so
    /// charging the budget would let a crash loop burn through all five retries without a single
    /// send being attempted.
    /// </para>
    /// </summary>
    [Fact]
    public void AStaleClaimIsReturnedToTheQueueWithoutConsumingAnAttempt()
    {
        var notification = Queued();
        Assert.False(notification.BeginSending(Now).IsError);

        var released = notification.ReleaseStaleClaim(Now.AddMinutes(5));

        Assert.False(released.IsError);
        Assert.True(notification.Status.IsQueued);
        Assert.Null(notification.ClaimedAtUtc);
        Assert.Equal(Now.AddMinutes(5), notification.NextAttemptAtUtc);
        Assert.Equal(0, notification.AttemptCount);
        Assert.True(notification.IsDueAt(Now.AddMinutes(5)));
    }

    [Fact]
    public void OnlyAClaimedNotificationHasAClaimToRelease()
    {
        Assert.Equal(
            "Notification.NotClaimed",
            Queued().ReleaseStaleClaim(Now).FirstError.Code);
    }

    // ------------------------------------------------------------------ the retry schedule

    /// <summary>
    /// <b>The backoff, as a schedule.</b> Exponential, roughly eight hours across five attempts.
    ///
    /// <para>
    /// Asserted directly rather than inferred from the job, because a backoff that quietly
    /// collapsed to zero would turn a provider outage into a denial of service <i>against the
    /// provider</i> — and that is not visible from reading a dispatcher loop.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    public void TheBackoffDoublesPerAttempt(int attempt, int expectedMinutes)
    {
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes), NotificationRequest.BackoffFor(attempt));
    }

    /// <summary>
    /// <b>Jitter matters more than it looks.</b> A provider outage queues thousands of
    /// notifications; without jitter every one retries in the same second and re-creates the
    /// outage the moment it clears.
    /// </summary>
    [Fact]
    public void JitterIsAddedToTheBackoff()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(17),
            NotificationRequest.BackoffFor(1, 17));

        Assert.True(Queued().Fail("x", Now, -1).IsError);
    }

    /// <summary>
    /// A transient failure goes back to the queue with a backoff, and the attempt is spent.
    /// </summary>
    [Fact]
    public void ATransientFailureIsRequeuedWithABackoff()
    {
        var notification = Queued();
        Assert.False(notification.BeginSending(Now).IsError);

        Assert.False(notification.Fail("connection refused", Now).IsError);

        Assert.True(notification.Status.IsQueued);
        Assert.Equal(1, notification.AttemptCount);
        Assert.Equal("connection refused", notification.Outcome);
        Assert.Equal(Now + TimeSpan.FromMinutes(1), notification.NextAttemptAtUtc);

        // Not due yet, which is the whole point of a backoff.
        Assert.False(notification.IsDueAt(Now));
        Assert.True(notification.IsDueAt(Now + TimeSpan.FromMinutes(1)));
    }

    // ------------------------------------------------------------------ the dead letter

    /// <summary>
    /// <b>The test that is A8's answer.</b> Five transient failures and the notification
    /// dead-letters — <i>and publishes an event saying so</i>.
    ///
    /// <para>
    /// "Notifications fail silently and forever" becomes "fail loudly and finitely" only if the
    /// failure leaves the module. A dead-lettered notification nobody is told about is the same
    /// defect with a nicer table.
    /// </para>
    /// </summary>
    [Fact]
    public void FiveTransientFailuresDeadLetterItAndPublishTheFailure()
    {
        var notification = Queued();

        for (var attempt = 1; attempt < NotificationRequest.MaxAttempts; attempt++)
        {
            Assert.False(notification.BeginSending(Now).IsError);
            Assert.False(notification.Fail("provider down", Now).IsError);
            Assert.True(notification.Status.IsQueued, $"should still be queued after {attempt}");
            Assert.Empty(notification.DomainEvents.OfType<NotificationUndeliverableEvent>());
        }

        Assert.False(notification.BeginSending(Now).IsError);
        Assert.False(notification.Fail("provider down", Now).IsError);

        Assert.True(notification.Status.IsDeadLettered);
        Assert.True(notification.Status.IsTerminal);
        Assert.Equal(NotificationRequest.MaxAttempts, notification.AttemptCount);
        Assert.Null(notification.NextAttemptAtUtc);

        var dead = Assert.Single(
            notification.DomainEvents.OfType<NotificationUndeliverableEvent>());

        Assert.False(dead.Definitive);
        Assert.Equal("sam@example.test", dead.Recipient);
        Assert.Equal("provider down", dead.Reason);
    }

    /// <summary>
    /// <b>A rejection is terminal and costs no attempt.</b> The same asymmetry as a payment
    /// decline: a definitive answer is not a failure, and retrying one wastes the budget a
    /// genuinely transient failure needs — while five attempts at a blocked domain is five chances
    /// to damage a sending reputation.
    /// </summary>
    [Fact]
    public void ARejectionIsTerminalAndConsumesNoAttempt()
    {
        var notification = Queued();
        Assert.False(notification.BeginSending(Now).IsError);

        Assert.False(notification.Reject("invalid recipient", Now).IsError);

        Assert.Equal(NotificationStatusValue.Rejected, notification.Status.Value);
        Assert.True(notification.Status.IsTerminal);
        Assert.Equal(0, notification.AttemptCount);
        Assert.Null(notification.NextAttemptAtUtc);

        var dead = Assert.Single(
            notification.DomainEvents.OfType<NotificationUndeliverableEvent>());

        // Definitive, so a consumer knows to ask the customer for a new address rather than wait.
        Assert.True(dead.Definitive);
    }

    [Fact]
    public void ATerminalNotificationIsNeverSentAgain()
    {
        var sent = Queued();
        Assert.False(sent.MarkSent(Now).IsError);

        Assert.True(sent.BeginSending(Now).IsError);
        Assert.True(sent.MarkSent(Now).IsError);
        Assert.True(sent.Fail("x", Now).IsError);
        Assert.True(sent.Reject("x", Now).IsError);
        Assert.False(sent.IsDueAt(Now.AddYears(1)));
    }

    // ------------------------------------------------------------------ A17: the delivery record

    /// <summary>
    /// <b>Every interaction leaves a numbered row.</b> A8's second sentence — "and nothing records
    /// that" — is why the whole defect survived unnoticed for the life of the system.
    /// </summary>
    [Fact]
    public void EveryAttemptLeavesANumberedRow()
    {
        var notification = Queued();

        notification.RecordAttempt(
            DeliveryOutcome.TransportFailure, "http", null, "c1", "connection refused", 15, Now);
        notification.RecordAttempt(
            DeliveryOutcome.TransportFailure, "http", null, "c2", "timeout", 10_000, Now.AddMinutes(1));
        notification.RecordAttempt(
            DeliveryOutcome.Accepted, "http", "msg_1", "c3", null, 120, Now.AddMinutes(3));

        Assert.Equal([1, 2, 3], notification.Attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(
            [DeliveryOutcome.TransportFailure, DeliveryOutcome.TransportFailure, DeliveryOutcome.Accepted],
            notification.Attempts.Select(attempt => attempt.Outcome));
        Assert.Equal(10_000, notification.Attempts.ElementAt(1).LatencyMs);
        Assert.Equal("msg_1", notification.Attempts.Last().ProviderMessageId);
    }

    /// <summary>
    /// <b>Which sender handled it is recorded, and that is not incidental.</b> "It was sent" means
    /// something very different for the development logging sender than for a real provider, and a
    /// delivery log that did not distinguish them is how a development default reaches production
    /// unnoticed — which would reproduce A8 exactly: every message recorded as delivered, nothing
    /// transmitted.
    /// </summary>
    [Fact]
    public void TheSenderIsRecordedOnEveryAttempt()
    {
        var notification = Queued();

        notification.RecordAttempt(DeliveryOutcome.Accepted, "logging", "log_1", "c1", null, 1, Now);

        Assert.Equal("logging", notification.Attempts.Single().Sender);
    }

    /// <summary>
    /// Attempt detail is bounded. An exception's <c>ToString()</c> can carry kilobytes of stack
    /// trace, and a dead-lettering notification writes five of them. BUGS-AND-LESSONS §9.
    /// </summary>
    [Fact]
    public void AttemptDetailIsBoundedSoItFitsItsColumn()
    {
        var notification = Queued();

        notification.RecordAttempt(
            DeliveryOutcome.TransportFailure,
            "http",
            null,
            "c1",
            new string('x', DeliveryAttempt.MaxDetailLength * 3),
            5,
            Now);

        Assert.Equal(
            DeliveryAttempt.MaxDetailLength, notification.Attempts.Single().Detail!.Length);
    }

    // ------------------------------------------------------------------ suppression

    /// <summary>
    /// <b>"We chose not to" and "we failed to" are different facts.</b> A suppressed notification
    /// is recorded with a reason, because a silently dropped one is precisely A8.
    /// </summary>
    [Fact]
    public void SuppressionIsRecordedWithAReason()
    {
        var notification = Queued();

        Assert.True(notification.Suppress("   ", Now).IsError);

        Assert.False(notification.Suppress("the customer has been anonymised", Now).IsError);
        Assert.Equal(NotificationStatusValue.Suppressed, notification.Status.Value);
        Assert.Equal("the customer has been anonymised", notification.Outcome);
        Assert.True(notification.Status.IsTerminal);
    }

    // ------------------------------------------------------------------ the secret body

    /// <summary>
    /// <b>A finished notification stops holding its token, and an unfinished one does not.</b>
    ///
    /// <para>
    /// The body of a verification or reset message is a live credential: the database stores only
    /// a hash, so the email is the single thing in existence that can deliver it. The token's own
    /// expiry is the primary control and this is defence in depth — because a notifications table
    /// is exactly the thing that gets copied into a reporting warehouse. Onyx <b>S14</b> stored
    /// reset tokens in plaintext with no expiry and no single-use marker at all.
    /// </para>
    ///
    /// <para>
    /// It must not fire while the notification is still queued, because a retry has to send the
    /// same message.
    /// </para>
    /// </summary>
    [Fact]
    public void OnlyAFinishedSecretBodyIsForgotten()
    {
        var queued = Queued(NotificationKind.PasswordReset, "reset-1", secret: true);

        // Still queued: the body is needed to send it.
        Assert.False(queued.ForgetBody());
        Assert.NotEqual(string.Empty, queued.Body);

        Assert.False(queued.MarkSent(Now).IsError);

        Assert.True(queued.ForgetBody());
        Assert.Equal(string.Empty, queued.Body);

        // The subject stays, so the audit trail still says what was sent.
        Assert.Equal("Subject", queued.Subject);

        // Idempotent: nothing left to forget.
        Assert.False(queued.ForgetBody());
    }

    /// <summary>A body with no secret in it is kept, because it is the record of what was said.</summary>
    [Fact]
    public void ANonSecretBodyIsNeverForgotten()
    {
        var notification = Queued(secret: false);
        Assert.False(notification.MarkSent(Now).IsError);

        Assert.False(notification.ForgetBody());
        Assert.Equal("Body", notification.Body);
    }

    /// <summary>
    /// The invariant <c>notifications.integrity-check</c> reports on — and it reports rather than
    /// clears, because silently clearing would hide a retention job that had stopped running,
    /// which is Onyx <b>D5</b>'s shape.
    /// </summary>
    [Fact]
    public void AStaleSecretIsDetectableAfterItsRetentionWindow()
    {
        var notification = Queued(NotificationKind.PasswordReset, "reset-1", secret: true);
        Assert.False(notification.MarkSent(Now).IsError);

        var grace = TimeSpan.FromHours(1);

        Assert.False(notification.HoldsAStaleSecret(Now, grace));
        Assert.False(notification.HoldsAStaleSecret(Now.AddMinutes(59), grace));
        Assert.True(notification.HoldsAStaleSecret(Now.AddHours(1), grace));

        notification.ForgetBody();

        Assert.False(notification.HoldsAStaleSecret(Now.AddDays(1), grace));
    }

    // ------------------------------------------------------------------ addresses

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nobody")]
    [InlineData("@example.test")]
    [InlineData("sam@")]
    [InlineData("sam@@example.test")]
    public void AMalformedAddressIsRefused(string? value)
    {
        Assert.True(NotificationAddress.Create(value).IsError);
    }

    [Fact]
    public void AnAddressIsTrimmedAndKeepsItsDisplayName()
    {
        var address = NotificationAddress.Create("  sam@example.test ", " Sam Shopper ").Value;

        Assert.Equal("sam@example.test", address.Value);
        Assert.Equal("Sam Shopper", address.DisplayName);
        Assert.Equal("Sam Shopper <sam@example.test>", address.ToString());
    }

    /// <summary>
    /// Addresses compare by address, not by display name — so the same inbox under two names is
    /// one recipient.
    /// </summary>
    [Fact]
    public void AddressesCompareByAddressAlone()
    {
        Assert.Equal(
            NotificationAddress.Create("sam@example.test", "Sam").Value,
            NotificationAddress.Create("sam@example.test", "Samantha").Value);
    }

    // ------------------------------------------------------------------ exhaustiveness

    /// <summary>
    /// <b>Every status has defined behaviour for every transition.</b>
    ///
    /// <para>
    /// Not an assertion about outcomes — an assertion that nothing throws, returns null, or falls
    /// through a <c>switch</c> without a case. Onyx <b>M1</b> is exactly a missing case: the code
    /// had no branch for "the provider did not say yes", so the one branch it did have ran
    /// regardless.
    /// </para>
    ///
    /// <para>
    /// Driven from <c>Enum.GetValues</c>, so a status added without being considered here fails.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryTransitionIsDefinedForEveryStatus()
    {
        foreach (var value in Enum.GetValues<NotificationStatusValue>())
        {
            var status = NotificationStatus.From(value);

            foreach (var (name, result) in new (string, ErrorOr.ErrorOr<NotificationStatus>)[]
                     {
                         ("BeginSending", status.BeginSending()),
                         ("MarkSent", status.MarkSent()),
                         ("Reject", status.Reject()),
                         ("Fail(spent)", status.Fail(true)),
                         ("Fail(remaining)", status.Fail(false)),
                         ("ReleaseStaleClaim", status.ReleaseStaleClaim()),
                         ("Suppress", status.Suppress())
                     })
            {
                if (result.IsError)
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(result.FirstError.Code),
                        $"{value}.{name} returned an error with no code.");
                }
                else
                {
                    Assert.NotNull(result.Value);
                }
            }
        }
    }
}
