using BoomboCard.Notifications.Contracts.IntegrationEvents;
using BoomboCard.Notifications.Data;
using BoomboCard.Notifications.Domain.NotificationAggregate.Events;
using BoomboCard.SharedKernel.Persistence.Outbox;
using MediatR;

namespace BoomboCard.Notifications.Application.IntegrationEvents;

/*
 * One translator, for one event.
 *
 * "A message was queued" and "a message was sent" stay inside the module: no other module can act
 * on either, and every published contract is a coupling maintained forever.
 *
 * What has to leave is a failure, because that is exactly what Onyx could not produce. A8:
 * "notifications fail silently and forever". A dead letter published through the outbox is a fact
 * somebody can alert on.
 */

/// <summary>
/// Publishes a notification that will never arrive.
///
/// <para>
/// Enqueued inside the transaction that dead-lettered it, so the outbox row and the status change
/// commit together — there is no state in which a notification is dead and nothing was told.
/// </para>
/// </summary>
internal sealed class OnNotificationUndeliverableEnqueueIntegrationEvent(
    IOutboxWriter<NotificationsDbContext> outbox)
    : INotificationHandler<NotificationUndeliverableEvent>
{
    public Task Handle(
        NotificationUndeliverableEvent notification,
        CancellationToken cancellationToken)
    {
        outbox.Enqueue(new NotificationUndeliverableIntegrationEvent(
            Guid.NewGuid(),
            notification.NotificationId,
            notification.Kind.ToString(),
            notification.Reference,
            notification.CustomerId,
            notification.Recipient,
            notification.Reason,
            notification.Definitive,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}
