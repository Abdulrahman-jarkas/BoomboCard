using BoomboCard.Sales.Contracts.IntegrationEvents;
using BoomboCard.Sales.Data;
using BoomboCard.Sales.Domain.CheckoutSessionAggregate.Events;
using BoomboCard.Sales.Domain.OrderAggregate.Events;
using BoomboCard.SharedKernel.Persistence.Outbox;
using MediatR;

namespace BoomboCard.Sales.Application.IntegrationEvents;

/*
 * The bridge from Sales' internal events to its published contracts.
 *
 * These are the messages the rest of the system has been waiting for: Loyalty's earn rule,
 * built and tested in Phase 3, had no trigger until OrderPaid existed.
 *
 * No I/O and no call into another context — these run inside the command's transaction, so the
 * outbox row commits with the state change that produced it. Onyx A9 dispatched its payment
 * notification BEFORE the payment row was added to the context, so a customer could be told
 * their payment had succeeded for a transaction that then rolled back.
 */

internal sealed class OnOrderPlacedEnqueueIntegrationEvent(IOutboxWriter<SalesDbContext> outbox)
    : INotificationHandler<OrderPlacedEvent>
{
    public Task Handle(OrderPlacedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new OrderPlacedIntegrationEvent(
            Guid.NewGuid(),
            notification.OrderId,
            notification.OrderNumber,
            notification.CustomerId,
            notification.Total,
            notification.CurrencyCode,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnOrderPaidEnqueueIntegrationEvent(IOutboxWriter<SalesDbContext> outbox)
    : INotificationHandler<OrderPaidEvent>
{
    public Task Handle(OrderPaidEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new OrderPaidIntegrationEvent(
            Guid.NewGuid(),
            notification.OrderId,
            notification.OrderNumber,
            notification.CustomerId,
            notification.Total,
            // Merchandise after discounts, excluding tax and shipping — computed once by
            // QuoteCalculator.QualifyingSpend and carried on the order, so Loyalty earns on the
            // figure that was actually charged rather than re-deriving it from parts.
            notification.QualifyingSpend,
            notification.CurrencyCode,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

/// <summary>
/// Publishes a shipment. <b>Phase 6's addition to Sales</b> — the domain event has existed since
/// Phase 4 and nothing translated it, because nothing outside Sales had asked. Notifications
/// asked.
/// </summary>
internal sealed class OnOrderShippedEnqueueIntegrationEvent(IOutboxWriter<SalesDbContext> outbox)
    : INotificationHandler<OrderShippedEvent>
{
    public Task Handle(OrderShippedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new OrderShippedIntegrationEvent(
            Guid.NewGuid(),
            notification.OrderId,
            notification.OrderNumber,
            notification.CustomerId,
            notification.UnitsShipped,
            // Inverted here rather than on the domain event, because "fully shipped" is the
            // question the aggregate asks itself and "is there more coming?" is the question a
            // consumer asks. Same fact, named for its audience.
            PartialShipment: !notification.FullyShipped,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnOrderDeliveredEnqueueIntegrationEvent(IOutboxWriter<SalesDbContext> outbox)
    : INotificationHandler<OrderDeliveredEvent>
{
    public Task Handle(OrderDeliveredEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new OrderDeliveredIntegrationEvent(
            Guid.NewGuid(),
            notification.OrderId,
            notification.OrderNumber,
            notification.CustomerId,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnOrderCancelledEnqueueIntegrationEvent(IOutboxWriter<SalesDbContext> outbox)
    : INotificationHandler<OrderCancelledEvent>
{
    public Task Handle(OrderCancelledEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new OrderCancelledIntegrationEvent(
            Guid.NewGuid(),
            notification.OrderId,
            notification.OrderNumber,
            notification.CustomerId,
            notification.CheckoutSessionId,
            notification.Reason,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnOrderRefundedEnqueueIntegrationEvent(IOutboxWriter<SalesDbContext> outbox)
    : INotificationHandler<OrderRefundedEvent>
{
    public Task Handle(OrderRefundedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new OrderRefundedIntegrationEvent(
            Guid.NewGuid(),
            notification.OrderId,
            notification.OrderNumber,
            notification.CustomerId,
            notification.Amount,
            notification.CurrencyCode,
            notification.RefundReference,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

/// <summary>
/// Expiry and cancellation both publish <c>CheckoutAbandonedIntegrationEvent</c>, because the
/// consumers' question is the same in both cases: "release whatever you were holding for this
/// session". <b>Recovery layer 2.</b>
/// </summary>
internal sealed class OnCheckoutExpiredEnqueueIntegrationEvent(IOutboxWriter<SalesDbContext> outbox)
    : INotificationHandler<CheckoutExpiredEvent>
{
    public Task Handle(CheckoutExpiredEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new CheckoutAbandonedIntegrationEvent(
            Guid.NewGuid(),
            notification.CheckoutSessionId,
            notification.CustomerId,
            "expired",
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnCheckoutCancelledEnqueueIntegrationEvent(
    IOutboxWriter<SalesDbContext> outbox)
    : INotificationHandler<CheckoutCancelledEvent>
{
    public Task Handle(CheckoutCancelledEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new CheckoutAbandonedIntegrationEvent(
            Guid.NewGuid(),
            notification.CheckoutSessionId,
            notification.CustomerId,
            notification.Reason,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}
