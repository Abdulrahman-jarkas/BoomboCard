using BoomboCard.Customers.Contracts.IntegrationEvents;
using BoomboCard.Customers.Data;
using BoomboCard.Customers.Domain.CustomerAggregate.Events;
using BoomboCard.SharedKernel.Persistence.Outbox;
using MediatR;

namespace BoomboCard.Customers.Application.Customers.IntegrationEvents;

/*
 * The bridge from internal domain events to published contracts.
 *
 * Each translator does exactly two things: flatten the domain event into a Contracts record,
 * and enqueue it. It performs NO I/O and calls NO other context, because it runs inside the
 * command's transaction. The outbox row commits with the aggregate change, and a background
 * dispatcher publishes it afterwards — which is what guarantees a side effect is dispatched
 * only after the fact that justified it is durable.
 *
 * Onyx A9 got that ordering backwards: its payment notification was dispatched before the
 * Payment row was even added to the context, so a customer could be told their payment had
 * succeeded for a transaction that then rolled back. And A8 meant the notification was
 * `_ = Task.Run(...)` onto a method that threw, so nobody was told anything, ever, and nothing
 * recorded that.
 *
 * Note which events have NO translator: CustomerLockedOutEvent, CustomerAddressAdded/Updated/
 * Removed, CustomerDefaultAddressChanged, CustomerProfileUpdatedEvent. They are raised and
 * recorded, and nothing outside this module needs to know yet. That is the rule the README
 * states: every state change raises an event, including ones nothing subscribes to, so adding
 * a listener later never means editing the aggregate.
 */

internal sealed class OnCustomerRegisteredEnqueueIntegrationEvent(
    IOutboxWriter<CustomersDbContext> outbox)
    : INotificationHandler<CustomerRegisteredEvent>
{
    public Task Handle(CustomerRegisteredEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new CustomerRegisteredIntegrationEvent(
            Guid.NewGuid(),
            notification.CustomerId,
            notification.Email,
            notification.Name,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnCustomerPasswordChangedEnqueueIntegrationEvent(
    IOutboxWriter<CustomersDbContext> outbox)
    : INotificationHandler<CustomerPasswordChangedEvent>
{
    public Task Handle(
        CustomerPasswordChangedEvent notification,
        CancellationToken cancellationToken)
    {
        outbox.Enqueue(new CustomerPasswordChangedIntegrationEvent(
            Guid.NewGuid(),
            notification.CustomerId,
            notification.Email,
            notification.ViaReset,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

/// <summary>
/// A privilege change, published with its actor attached so the audit trail records who did it
/// rather than only that it happened. Onyx A40: "no audit trail of any kind".
/// </summary>
internal sealed class OnCustomerRoleChangedEnqueueIntegrationEvent(
    IOutboxWriter<CustomersDbContext> outbox)
    : INotificationHandler<CustomerRoleChangedEvent>
{
    public Task Handle(CustomerRoleChangedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new CustomerRoleChangedIntegrationEvent(
            Guid.NewGuid(),
            notification.CustomerId,
            notification.PreviousRole,
            notification.NewRole,
            notification.ChangedByCustomerId,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

/// <summary>
/// The message that makes erasure actually reach the copies. Other modules hold the name and
/// address — a notification log, a reporting projection — and a <c>DELETE</c> in this database
/// cannot touch theirs. Onyx D13 deleted the row and left every copy standing.
/// </summary>
internal sealed class OnCustomerAnonymisedEnqueueIntegrationEvent(
    IOutboxWriter<CustomersDbContext> outbox)
    : INotificationHandler<CustomerAnonymisedEvent>
{
    public Task Handle(CustomerAnonymisedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new CustomerAnonymisedIntegrationEvent(
            Guid.NewGuid(),
            notification.CustomerId,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}
