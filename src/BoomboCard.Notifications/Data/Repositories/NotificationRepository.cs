using BoomboCard.Notifications.Domain.NotificationAggregate;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Notifications.Data.Repositories;

/// <summary>
/// Note the absence of <c>SaveChangesAsync</c>. Persisting is the unit of work's job — Onyx
/// <b>D3</b> committed order creation N+2 separate times.
/// </summary>
internal interface INotificationRepository
{
    void Add(NotificationRequest notification);

    Task<NotificationRequest?> GetAsync(
        Guid notificationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this exact notification already exists. The read in front of
    /// <c>UX_Notifications_Idempotency</c>, which is the actual guarantee.
    /// </summary>
    Task<bool> ExistsAsync(
        NotificationKind kind,
        string reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifications due for a send, oldest first and capped. The dispatcher's query, and the
    /// same shape as the outbox's.
    /// </summary>
    Task<List<Guid>> GetDueIdsAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims nobody completed. A process killed between claiming and sending leaves a row in
    /// <c>Sending</c>; without this sweep it stays there forever, which is Onyx's "silently and
    /// forever" reached by a different road.
    /// </summary>
    Task<List<Guid>> GetStaleClaimIdsAsync(
        DateTimeOffset claimedBeforeUtc,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finished notifications still holding a rendered secret. The retention job's query.
    /// </summary>
    Task<List<Guid>> GetStaleSecretIdsAsync(
        DateTimeOffset finishedBeforeUtc,
        int limit,
        CancellationToken cancellationToken = default);
}

internal sealed class NotificationRepository(NotificationsDbContext context)
    : INotificationRepository
{
    public void Add(NotificationRequest notification) => context.Notifications.Add(notification);

    public Task<NotificationRequest?> GetAsync(
        Guid notificationId,
        CancellationToken cancellationToken = default) =>
        context.Notifications
            .Include(notification => notification.Attempts)
            .FirstOrDefaultAsync(
                notification => notification.Id == notificationId, cancellationToken);

    public Task<bool> ExistsAsync(
        NotificationKind kind,
        string reference,
        CancellationToken cancellationToken = default) =>
        context.Notifications.AnyAsync(
            notification => notification.Kind == kind && notification.Reference == reference,
            cancellationToken);

    public Task<List<Guid>> GetDueIdsAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken = default) =>
        context.Notifications
            .Where(notification =>
                notification.Status.Value == NotificationStatusValue.Queued
                && notification.NextAttemptAtUtc != null
                && notification.NextAttemptAtUtc <= nowUtc)
            // Ordered because it is capped — an unordered Take could starve the same notification
            // pass after pass, and one of these might be the password reset a locked-out customer
            // is waiting for. BUGS-AND-LESSONS §8; the EF warning for this now throws.
            .OrderBy(notification => notification.NextAttemptAtUtc)
            .Take(limit)
            .Select(notification => notification.Id)
            .ToListAsync(cancellationToken);

    public Task<List<Guid>> GetStaleClaimIdsAsync(
        DateTimeOffset claimedBeforeUtc,
        int limit,
        CancellationToken cancellationToken = default) =>
        context.Notifications
            .Where(notification =>
                notification.Status.Value == NotificationStatusValue.Sending
                && notification.ClaimedAtUtc != null
                && notification.ClaimedAtUtc <= claimedBeforeUtc)
            .OrderBy(notification => notification.ClaimedAtUtc)
            .Take(limit)
            .Select(notification => notification.Id)
            .ToListAsync(cancellationToken);

    public Task<List<Guid>> GetStaleSecretIdsAsync(
        DateTimeOffset finishedBeforeUtc,
        int limit,
        CancellationToken cancellationToken = default) =>
        context.Notifications
            .Where(notification =>
                notification.ContainsSecret
                && notification.Body != string.Empty
                && notification.FinishedAtUtc != null
                && notification.FinishedAtUtc <= finishedBeforeUtc)
            .OrderBy(notification => notification.FinishedAtUtc)
            .Take(limit)
            .Select(notification => notification.Id)
            .ToListAsync(cancellationToken);
}
