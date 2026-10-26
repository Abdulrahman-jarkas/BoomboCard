using BoomboCard.Notifications.Data.Repositories;
using BoomboCard.Notifications.Domain.NotificationAggregate;
using BoomboCard.Notifications.Domain.Templates;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Notifications.Tests;

/// <summary>
/// What the database guarantees, asserted against the database.
///
/// <para>
/// One of these matters more than the rest: <c>UX_Notifications_Idempotency</c> is the reason a
/// customer gets one email rather than three. The inbox claim deduplicates a redelivered
/// integration event, and this index covers what the inbox cannot — two <i>different</i> events
/// that both want to say the same thing, a replayed outbox after a restore, and a consumer
/// registered twice by a wiring mistake.
/// </para>
/// </summary>
public sealed class NotificationsModelTests(SqlServerFixture fixture)
    : IClassFixture<SqlServerFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 9, 0, 0, TimeSpan.Zero);

    private static NotificationRequest Queued(
        NotificationKind kind,
        string reference,
        bool secret = false,
        Guid? customerId = null,
        DateTimeOffset? at = null) =>
        NotificationRequest.Queue(
            kind,
            NotificationChannel.Email,
            reference,
            customerId ?? Guid.NewGuid(),
            NotificationAddress.Create("sam@example.test", "Sam").Value,
            new RenderedNotification("Subject", "Body", secret),
            at ?? Now).Value;

    // ----------------------------------------------------------------- the guarantee

    /// <summary>
    /// <b>One notification per (Kind, Reference), ever.</b>
    ///
    /// <para>
    /// The handler's existence check is the fast path and this index is the guarantee: two
    /// consumers racing both pass the read, and the second insert is rejected. Onyx <b>A8</b> had
    /// nothing here at all — no retry, no dead-letter, no delivery record — so a duplicate send
    /// was not even a concept that could be discussed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSameKindAndReferenceCannotBeQueuedTwice()
    {
        await using (var setup = fixture.CreateContext())
        {
            setup.Notifications.Add(Queued(NotificationKind.OrderPlaced, "dup-order-1"));
            await setup.SaveChangesAsync();
        }

        await using var second = fixture.CreateContext();
        second.Notifications.Add(Queued(NotificationKind.OrderPlaced, "dup-order-1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    /// <summary>
    /// <b>The same reference under a different kind is fine, and that is the point of the pair.</b>
    ///
    /// <para>
    /// One order legitimately produces a confirmation, a shipment notice, a delivery notice and
    /// possibly a cancellation — all keyed on the same order id. Uniqueness on the reference alone
    /// would send the first and silently swallow the rest, which is a worse failure than a
    /// duplicate because nobody would ever notice.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSameReferenceUnderADifferentKindIsAllowed()
    {
        await using var context = fixture.CreateContext();

        context.Notifications.Add(Queued(NotificationKind.OrderPlaced, "multi-order-1"));
        context.Notifications.Add(Queued(NotificationKind.OrderShipped, "multi-order-1"));
        context.Notifications.Add(Queued(NotificationKind.OrderDelivered, "multi-order-1"));
        context.Notifications.Add(Queued(NotificationKind.OrderCancelled, "multi-order-1"));

        await context.SaveChangesAsync();

        var count = await context.Notifications
            .CountAsync(notification => notification.Reference == "multi-order-1");

        Assert.Equal(4, count);
    }

    /// <summary>
    /// The read in front of the index agrees with it — otherwise the fast path would be a
    /// different rule from the guarantee.
    /// </summary>
    [Fact]
    public async Task TheExistenceCheckMatchesTheIndex()
    {
        await using (var setup = fixture.CreateContext())
        {
            setup.Notifications.Add(Queued(NotificationKind.PaymentReceived, "exists-1"));
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();
        var repository = new NotificationRepository(context);

        Assert.True(await repository.ExistsAsync(NotificationKind.PaymentReceived, "exists-1"));
        Assert.False(await repository.ExistsAsync(NotificationKind.PaymentDeclined, "exists-1"));
        Assert.False(await repository.ExistsAsync(NotificationKind.PaymentReceived, "exists-2"));
    }

    // ----------------------------------------------------------------- the dispatcher's query

    /// <summary>
    /// <b>The dispatcher only sees what is due.</b>
    ///
    /// <para>
    /// A notification whose backoff has not elapsed, one already claimed, and one already sent
    /// must all be invisible — otherwise "retry politely" becomes a tight loop against a provider
    /// that is already struggling.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnlyDueNotificationsAreDispatched()
    {
        var due = Queued(NotificationKind.OrderPlaced, "due-1");

        var backedOff = Queued(NotificationKind.OrderPlaced, "due-2");
        backedOff.BeginSending(Now);
        backedOff.Fail("provider down", Now);

        var claimed = Queued(NotificationKind.OrderPlaced, "due-3");
        claimed.BeginSending(Now);

        var sent = Queued(NotificationKind.OrderPlaced, "due-4");
        sent.MarkSent(Now);

        await using (var setup = fixture.CreateContext())
        {
            setup.Notifications.AddRange(due, backedOff, claimed, sent);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();
        var ids = await new NotificationRepository(context).GetDueIdsAsync(Now, 50);

        Assert.Contains(due.Id, ids);
        Assert.DoesNotContain(backedOff.Id, ids);
        Assert.DoesNotContain(claimed.Id, ids);
        Assert.DoesNotContain(sent.Id, ids);

        // And the backed-off one becomes visible once its minute has passed.
        var later = await new NotificationRepository(context)
            .GetDueIdsAsync(Now.AddMinutes(2), 50);

        Assert.Contains(backedOff.Id, later);
    }

    /// <summary>
    /// Oldest first, because the batch is capped. An unordered <c>Take</c> could starve the same
    /// notification pass after pass — and one of these might be the password reset a locked-out
    /// customer is waiting for. BUGS-AND-LESSONS §8.
    /// </summary>
    [Fact]
    public async Task TheDueQueueIsOrderedOldestFirst()
    {
        var oldest = Queued(NotificationKind.PointsEarned, "order-a", at: Now);
        var middle = Queued(NotificationKind.PointsEarned, "order-b", at: Now.AddMinutes(1));
        var newest = Queued(NotificationKind.PointsEarned, "order-c", at: Now.AddMinutes(2));

        await using (var setup = fixture.CreateContext())
        {
            // Inserted newest-first, so insertion order cannot accidentally satisfy the test.
            setup.Notifications.AddRange(newest, middle, oldest);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();

        var ids = await new NotificationRepository(context)
            .GetDueIdsAsync(Now.AddMinutes(10), limit: 500);

        // The RELATIVE order of these three, not the absolute first two.
        //
        // The fixture is per test CLASS, so other tests' queued rows are in this table and are
        // also due — an earlier draft asserted `[oldest, middle]` and failed because a row from
        // another test sorted ahead of both. That is the shape of the Phase 3 bug recorded in
        // BUGS-AND-LESSONS §6: a query scoped more loosely than the test's own data.
        var positions = new[] { oldest.Id, middle.Id, newest.Id }
            .Select(id => ids.IndexOf(id))
            .ToList();

        Assert.All(positions, position => Assert.True(position >= 0, "all three should be due"));
        Assert.True(
            positions[0] < positions[1] && positions[1] < positions[2],
            $"expected oldest-first ordering, got positions {string.Join(", ", positions)}.");
    }

    /// <summary>
    /// <b>BUGS-AND-LESSONS §8, promoted to a thrown exception.</b> A capped query with no
    /// <c>ORDER BY</c> returns an arbitrary page. This asserts the guard is wired on this module's
    /// context rather than only on the ones where it was first needed.
    /// </summary>
    [Fact]
    public async Task ACappedQueryWithNoOrderByThrows()
    {
        await using var context = fixture.CreateContext();

        await Assert.ThrowsAnyAsync<Exception>(() => context.Notifications.Take(5).ToListAsync());
    }

    // ----------------------------------------------------------------- the sweeps

    /// <summary>
    /// The stale-claim sweep finds claims nobody completed, and leaves fresh ones alone.
    /// </summary>
    [Fact]
    public async Task OnlyStaleClaimsAreSwept()
    {
        var stale = Queued(NotificationKind.PasswordChanged, "stale-1");
        stale.BeginSending(Now);

        var fresh = Queued(NotificationKind.PasswordChanged, "stale-2");
        fresh.BeginSending(Now.AddMinutes(5));

        await using (var setup = fixture.CreateContext())
        {
            setup.Notifications.AddRange(stale, fresh);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();

        var ids = await new NotificationRepository(context)
            .GetStaleClaimIdsAsync(Now.AddMinutes(2), 50);

        Assert.Contains(stale.Id, ids);
        Assert.DoesNotContain(fresh.Id, ids);
    }

    /// <summary>
    /// <b>The retention sweep finds exactly the finished notifications still holding a token.</b>
    ///
    /// <para>
    /// Not the ones with no secret, not the ones still queued — a queued one needs its body to be
    /// sent — and not the ones inside the retention window.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OnlyFinishedSecretBodiesPastRetentionAreSwept()
    {
        var ripe = Queued(NotificationKind.PasswordReset, "secret-ripe", secret: true);
        ripe.MarkSent(Now);

        var tooRecent = Queued(NotificationKind.PasswordReset, "secret-recent", secret: true);
        tooRecent.MarkSent(Now.AddMinutes(50));

        var stillQueued = Queued(NotificationKind.PasswordReset, "secret-queued", secret: true);

        var notSecret = Queued(NotificationKind.OrderPlaced, "secret-none");
        notSecret.MarkSent(Now);

        await using (var setup = fixture.CreateContext())
        {
            setup.Notifications.AddRange(ripe, tooRecent, stillQueued, notSecret);
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.CreateContext();

        var ids = await new NotificationRepository(context)
            .GetStaleSecretIdsAsync(Now.AddHours(1) - TimeSpan.FromHours(1), 50);

        Assert.Contains(ripe.Id, ids);
        Assert.DoesNotContain(tooRecent.Id, ids);
        Assert.DoesNotContain(stillQueued.Id, ids);
        Assert.DoesNotContain(notSecret.Id, ids);
    }

    /// <summary>
    /// A forgotten body actually leaves the database, which is the whole point. A test that only
    /// checked the in-memory aggregate would pass with the row untouched.
    /// </summary>
    [Fact]
    public async Task AForgottenBodyIsGoneFromTheRow()
    {
        var notification = Queued(NotificationKind.EmailVerification, "forget-1", secret: true);
        notification.MarkSent(Now);

        await using (var setup = fixture.CreateContext())
        {
            setup.Notifications.Add(notification);
            await setup.SaveChangesAsync();
        }

        await using (var sweep = fixture.CreateContext())
        {
            var loaded = await sweep.Notifications.FirstAsync(
                candidate => candidate.Id == notification.Id);

            Assert.True(loaded.ForgetBody());
            await sweep.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();

        var row = await read.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == notification.Id)
            .Select(candidate => new { candidate.Subject, candidate.Body, candidate.ContainsSecret })
            .FirstAsync();

        Assert.Equal(string.Empty, row.Body);
        Assert.Equal("Subject", row.Subject);
        Assert.True(row.ContainsSecret);
    }

    // ----------------------------------------------------------------- round trips

    /// <summary>
    /// A body at the stated maximum survives the round trip.
    ///
    /// <para>
    /// BUGS-AND-LESSONS §9 is why this is a test rather than an assumption: the outbox payload
    /// column inherited a model-wide 500-character convention and was two fields from silently
    /// failing every integration event. The aggregate refuses anything longer, so the column and
    /// the invariant have to agree exactly.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ABodyAtTheMaximumLengthRoundTripsIntact()
    {
        var body = new string('x', NotificationRequest.MaxBodyLength);
        var subject = new string('s', NotificationRequest.MaxSubjectLength);

        var notification = NotificationRequest.Queue(
            NotificationKind.OrderPlaced,
            NotificationChannel.Email,
            "max-length-1",
            Guid.NewGuid(),
            NotificationAddress.Create("sam@example.test").Value,
            new RenderedNotification(subject, body, false),
            Now).Value;

        await using (var setup = fixture.CreateContext())
        {
            setup.Notifications.Add(notification);
            await setup.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();

        var row = await read.Notifications
            .AsNoTracking()
            .FirstAsync(candidate => candidate.Id == notification.Id);

        Assert.Equal(NotificationRequest.MaxBodyLength, row.Body.Length);
        Assert.Equal(NotificationRequest.MaxSubjectLength, row.Subject.Length);
    }

    /// <summary>
    /// The attempt log round-trips with its sender, ordering and provider id — the delivery record
    /// Onyx <b>A8</b> says did not exist.
    /// </summary>
    [Fact]
    public async Task TheDeliveryRecordRoundTripsInOrder()
    {
        var notification = Queued(NotificationKind.OrderShipped, "attempts-1");

        notification.RecordAttempt(
            DeliveryOutcome.TransportFailure, "http", null, "c1", "connection refused", 12, Now);
        notification.RecordAttempt(
            DeliveryOutcome.Accepted, "logging", "log_abc", "c2", null, 3, Now.AddMinutes(1));

        await using (var setup = fixture.CreateContext())
        {
            setup.Notifications.Add(notification);
            await setup.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();

        var attempts = await read.DeliveryAttempts
            .AsNoTracking()
            .Where(attempt => attempt.NotificationId == notification.Id)
            .OrderBy(attempt => attempt.AttemptNumber)
            .ToListAsync();

        Assert.Equal(2, attempts.Count);
        Assert.Equal("http", attempts[0].Sender);
        Assert.Equal(DeliveryOutcome.TransportFailure, attempts[0].Outcome);
        Assert.Equal("logging", attempts[1].Sender);
        Assert.Equal("log_abc", attempts[1].ProviderMessageId);
        Assert.Equal(12, attempts[0].LatencyMs);
    }

    /// <summary>
    /// The status is a readable constrained name in the table, not a number.
    ///
    /// <para>
    /// Onyx <b>A39</b>'s <c>Payment.Status</c> was free text; this is the constrained version of
    /// the same readability, and storing the name means a renumbered enum cannot silently
    /// reinterpret existing rows.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheStatusAndKindArePersistedAsNames()
    {
        var notification = Queued(NotificationKind.RefundIssued, "names-1");
        notification.MarkSent(Now);

        await using (var setup = fixture.CreateContext())
        {
            setup.Notifications.Add(notification);
            await setup.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();

        var found = await read.Notifications
            .AsNoTracking()
            .Where(candidate =>
                candidate.Kind == NotificationKind.RefundIssued
                && candidate.Status.Value == NotificationStatusValue.Sent)
            .CountAsync();

        Assert.Equal(1, found);
    }
}
