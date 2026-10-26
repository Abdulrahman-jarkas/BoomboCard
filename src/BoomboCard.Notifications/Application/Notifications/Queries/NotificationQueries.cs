using BoomboCard.Notifications.Contracts;
using BoomboCard.Notifications.Data;
using BoomboCard.Notifications.Domain.NotificationAggregate;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Notifications.Application.Notifications.Queries;

/*
 * Queries. None implement INotificationsRequest, so none take a transaction. Every one projects
 * into an explicit response record in SQL — no aggregate is reachable from any of them.
 *
 * AND NONE OF THEM PROJECTS THE BODY.
 *
 * That is not an oversight to be corrected later. A verification or reset body holds a plaintext
 * token, and an endpoint that returned it would hand a live credential to anybody who could read
 * a notification — which is Onyx S11 with a different column: it serialised its Customer entity
 * straight to the wire from three endpoints, so every response carried PasswordHash.
 *
 * The subject is exposed, because "Reset your password" is what makes a support conversation
 * possible and it contains nothing secret.
 */

/// <summary>One delivery attempt, as support sees it.</summary>
public sealed record DeliveryAttemptResponse(
    int AttemptNumber,
    string Outcome,
    string Sender,
    string? ProviderMessageId,
    string CorrelationId,
    int LatencyMs,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// A notification with its full delivery history.
///
/// <para>
/// <b>This response is the answer to Onyx A8's second sentence.</b> "Nothing records that" —
/// there is now a row per notification, a row per attempt, and an endpoint that shows both.
/// </para>
///
/// <para>
/// <c>Detail</c> is deliberately absent from the attempt rows: it holds provider messages and our
/// own exception text, which is diagnostic data for an operator reading logs rather than something
/// to return over HTTP. Onyx <b>S10</b> wrote <c>feature.Error.ToString()</c> into the response
/// body in every environment, leaking file paths, class names and SQL.
/// </para>
/// </summary>
public sealed record NotificationDetailResponse(
    Guid NotificationId,
    string Kind,
    string Channel,
    string Reference,
    Guid? CustomerId,
    string Recipient,
    string Subject,
    string Status,
    int AttemptCount,
    string? Outcome,
    bool ContainsSecret,
    bool BodyRetained,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    DateTimeOffset? SentAtUtc,
    IReadOnlyList<DeliveryAttemptResponse> Attempts);

public sealed record GetNotificationQuery(Guid NotificationId)
    : IRequest<ErrorOr<NotificationDetailResponse>>;

internal sealed class GetNotificationQueryHandler(NotificationsDbContext context)
    : IRequestHandler<GetNotificationQuery, ErrorOr<NotificationDetailResponse>>
{
    public async Task<ErrorOr<NotificationDetailResponse>> Handle(
        GetNotificationQuery query,
        CancellationToken cancellationToken)
    {
        var notification = await context.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == query.NotificationId)
            .Select(candidate => new NotificationDetailResponse(
                candidate.Id,
                candidate.Kind.ToString(),
                candidate.Channel.ToString(),
                candidate.Reference,
                candidate.CustomerId,
                candidate.Recipient.Value,
                candidate.Subject,
                candidate.Status.Value.ToString(),
                candidate.AttemptCount,
                candidate.Outcome,
                candidate.ContainsSecret,
                // Whether the body is still held, without returning it. Enough for an operator to
                // know whether the retention sweep has run, and useless to anybody who wants the
                // token.
                candidate.Body != string.Empty,
                candidate.QueuedAtUtc,
                candidate.NextAttemptAtUtc,
                candidate.SentAtUtc,
                candidate.Attempts
                    .OrderBy(attempt => attempt.OccurredAtUtc)
                    .Select(attempt => new DeliveryAttemptResponse(
                        attempt.AttemptNumber,
                        attempt.Outcome.ToString(),
                        attempt.Sender,
                        attempt.ProviderMessageId,
                        attempt.CorrelationId,
                        attempt.LatencyMs,
                        attempt.OccurredAtUtc))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);

        return notification is null
            ? NotificationErrors.NotFound(query.NotificationId)
            : notification;
    }
}

internal sealed class GetNotificationSummaryQueryHandler(NotificationsDbContext context)
    : IRequestHandler<GetNotificationSummaryQuery, ErrorOr<NotificationSummaryResponse>>
{
    public async Task<ErrorOr<NotificationSummaryResponse>> Handle(
        GetNotificationSummaryQuery query,
        CancellationToken cancellationToken)
    {
        var notification = await context.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == query.NotificationId)
            .Select(candidate => new NotificationSummaryResponse(
                candidate.Id,
                candidate.Kind.ToString(),
                candidate.Channel.ToString(),
                candidate.Reference,
                candidate.CustomerId,
                candidate.Status.Value.ToString(),
                candidate.AttemptCount,
                candidate.Outcome,
                candidate.QueuedAtUtc,
                candidate.SentAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

        return notification is null
            ? NotificationErrors.NotFound(query.NotificationId)
            : notification;
    }
}

/// <summary>
/// How the queue is doing.
///
/// <para>
/// <b>The query that makes Onyx A8 a five-second discovery rather than an audit finding.</b> "No
/// customer has ever received any communication from this system" is something a system with this
/// endpoint tells you the first time you look at it.
/// </para>
/// </summary>
internal sealed class GetNotificationHealthQueryHandler(NotificationsDbContext context)
    : IRequestHandler<GetNotificationHealthQuery, ErrorOr<NotificationHealthResponse>>
{
    public async Task<ErrorOr<NotificationHealthResponse>> Handle(
        GetNotificationHealthQuery query,
        CancellationToken cancellationToken)
    {
        // Counted by the database, in one grouped query. Onyx A26 ran one extra query per row
        // from inside a Select over an already-materialised list.
        var counts = await context.Notifications
            .AsNoTracking()
            .GroupBy(notification => notification.Status.Value)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var oldestQueued = await context.Notifications
            .AsNoTracking()
            .Where(notification => notification.Status.Value == NotificationStatusValue.Queued)
            .OrderBy(notification => notification.QueuedAtUtc)
            .Select(notification => (DateTimeOffset?)notification.QueuedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        int For(NotificationStatusValue status) =>
            counts.FirstOrDefault(row => row.Status == status)?.Count ?? 0;

        return new NotificationHealthResponse(
            For(NotificationStatusValue.Queued),
            For(NotificationStatusValue.Sending),
            For(NotificationStatusValue.DeadLettered),
            For(NotificationStatusValue.Rejected),
            oldestQueued);
    }
}

public sealed record NotificationListItem(
    Guid NotificationId,
    string Kind,
    string Recipient,
    string Subject,
    string Status,
    int AttemptCount,
    DateTimeOffset QueuedAtUtc);

public sealed record NotificationPage(
    IReadOnlyList<NotificationListItem> Items,
    string? NextCursor);

/// <summary>
/// A page of notifications, newest first, keyset paged with a capped limit.
///
/// Onyx <b>A27</b> did <c>.Skip().Take()</c> with no <c>ORDER BY</c> and <b>A28</b> left the page
/// size unvalidated.
/// </summary>
public sealed record SearchNotificationsQuery(
    string? Status = null,
    Guid? CustomerId = null,
    string? Cursor = null,
    int Limit = 25) : IRequest<ErrorOr<NotificationPage>>;

internal sealed class SearchNotificationsQueryHandler(NotificationsDbContext context)
    : IRequestHandler<SearchNotificationsQuery, ErrorOr<NotificationPage>>
{
    private const int MaxLimit = 100;

    public async Task<ErrorOr<NotificationPage>> Handle(
        SearchNotificationsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            return Error.Validation(
                "SearchNotifications.InvalidLimit", $"Limit must be between 1 and {MaxLimit}.");
        }

        var notifications = context.Notifications.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse<NotificationStatusValue>(query.Status, true, out var status))
            {
                return Error.Validation(
                    "SearchNotifications.UnknownStatus",
                    $"'{query.Status}' is not a notification status.");
            }

            notifications = notifications.Where(
                notification => notification.Status.Value == status);
        }

        if (query.CustomerId is { } customerId)
        {
            notifications = notifications.Where(
                notification => notification.CustomerId == customerId);
        }

        if (!string.IsNullOrWhiteSpace(query.Cursor)
            && DateTimeOffset.TryParse(
                query.Cursor,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var cursor))
        {
            notifications = notifications.Where(
                notification => notification.QueuedAtUtc < cursor);
        }

        var page = await notifications
            .OrderByDescending(notification => notification.QueuedAtUtc)
            .Take(query.Limit + 1)
            .Select(notification => new NotificationListItem(
                notification.Id,
                notification.Kind.ToString(),
                notification.Recipient.Value,
                notification.Subject,
                notification.Status.Value.ToString(),
                notification.AttemptCount,
                notification.QueuedAtUtc))
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > query.Limit;
        var items = hasMore ? page[..query.Limit] : page;

        return new NotificationPage(
            items, hasMore ? items[^1].QueuedAtUtc.ToString("O") : null);
    }
}
