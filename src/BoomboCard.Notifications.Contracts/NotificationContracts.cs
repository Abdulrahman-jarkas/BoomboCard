using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Notifications.Contracts;

/// <summary>
/// Marks a request as a Notifications command: it mutates Notifications state, and so must run
/// inside a transaction on this module's context.
///
/// <para>
/// <b>Note which command deliberately does NOT carry this marker.</b>
/// <c>DispatchNotificationCommand</c> calls a notification provider — over SMTP or HTTP — and a
/// transaction must never be held across that call. Onyx <b>A12</b> held one across up to three
/// gateway calls at a 100-second timeout each, which on a single-writer database blocked every
/// write in the system for up to five minutes per request. The same rule applies to a mail
/// server, which is if anything slower and more prone to hanging.
/// </para>
/// </summary>
public interface INotificationsRequest : ITransactionalRequest;

/// <summary>
/// Where a notification got to, as the rest of the system is allowed to see it.
///
/// <para>
/// Note the absence of <c>Body</c>. A verification or reset body holds a plaintext token, and a
/// contract that exposed it would make every consumer a credential holder — Onyx <b>S11</b>
/// serialised its <c>Customer</c> entity straight to the wire from three endpoints, so every
/// response carried <c>PasswordHash</c>.
/// </para>
/// </summary>
public sealed record NotificationSummaryResponse(
    Guid NotificationId,
    string Kind,
    string Channel,
    string Reference,
    Guid? CustomerId,
    string Status,
    int AttemptCount,
    string? Outcome,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? SentAtUtc);

/// <summary>A query, so no transaction.</summary>
public sealed record GetNotificationSummaryQuery(Guid NotificationId)
    : IRequest<ErrorOr<NotificationSummaryResponse>>;

/// <summary>
/// How many notifications are stuck, and how many never made it.
///
/// <para>
/// Published rather than kept internal because this is the number that answers Onyx <b>A8</b>'s
/// second sentence — <i>"and nothing records that"</i>. Phase 7's operational view reads it, and
/// a system where it can be asked is a system where "no customer has ever received any
/// communication" is a five-second discovery rather than an audit finding.
/// </para>
/// </summary>
public sealed record NotificationHealthResponse(
    int Queued,
    int Sending,
    int DeadLettered,
    int Rejected,
    DateTimeOffset? OldestQueuedAtUtc);

public sealed record GetNotificationHealthQuery : IRequest<ErrorOr<NotificationHealthResponse>>;
