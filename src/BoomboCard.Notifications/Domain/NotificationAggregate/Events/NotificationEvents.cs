using BoomboCard.SharedKernel;

namespace BoomboCard.Notifications.Domain.NotificationAggregate.Events;

/*
 * Notifications' internal events.
 *
 * Note how few of these are translated to published contracts. Nobody outside this module needs
 * to know that a message was queued or sent — publishing it would advertise a fact no consumer
 * can act on, and every published contract is a coupling maintained forever.
 *
 * The one that IS published is the undeliverable one, because that is the fact Onyx could not
 * produce: A8's "notifications fail silently and forever" becomes "fail loudly and finitely" only
 * if the failure leaves the module.
 */

public sealed record NotificationQueuedEvent(
    Guid NotificationId,
    NotificationKind Kind,
    string Reference,
    Guid? CustomerId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record NotificationSentEvent(
    Guid NotificationId,
    NotificationKind Kind,
    string Reference,
    Guid? CustomerId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// A notification will not be delivered — rejected outright, or dead-lettered after the retry
/// budget was spent.
///
/// <para>
/// <b>The event that makes this module different from what it replaces.</b> Onyx's notifications
/// failed with no retry, no dead-letter and no delivery record; the register's phrase is "silently
/// and forever". A dead letter published as an integration event is a fact another module can
/// alert on, and the difference between a silent failure and a loud one is whether anybody finds
/// out before a customer does.
/// </para>
///
/// <para>
/// <paramref name="Definitive"/> separates the two cases, because they need different responses.
/// A rejection means the address is wrong and somebody should ask the customer for a new one. A
/// dead letter after five transport failures means the provider was down and the message is
/// genuinely lost — which, for a password reset, means the customer is locked out and does not
/// know why.
/// </para>
/// </summary>
public sealed record NotificationUndeliverableEvent(
    Guid NotificationId,
    NotificationKind Kind,
    string Reference,
    Guid? CustomerId,
    string Recipient,
    string Reason,
    bool Definitive,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
