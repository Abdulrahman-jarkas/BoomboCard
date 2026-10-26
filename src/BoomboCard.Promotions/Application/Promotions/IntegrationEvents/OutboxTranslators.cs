using BoomboCard.Promotions.Contracts.IntegrationEvents;
using BoomboCard.Promotions.Data;
using BoomboCard.Promotions.Domain.PromotionAggregate;
using BoomboCard.Promotions.Domain.PromotionAggregate.Events;
using BoomboCard.SharedKernel.Persistence.Outbox;
using MediatR;

namespace BoomboCard.Promotions.Application.Promotions.IntegrationEvents;

/*
 * The bridge from internal domain events to published contracts.
 *
 * Each translator flattens the domain event into a Contracts record and enqueues it. No I/O, no
 * call into another context — these run inside the command's transaction, so the outbox row
 * commits with the state change that produced it.
 *
 * Note which events have NO translator: PromotionCreatedEvent and PromotionReconfiguredEvent.
 * A draft being created or edited is nobody else's business. They are still raised, because the
 * event is the record of the fact and adding an audit projection later must not require editing
 * the aggregate.
 */

internal sealed class OnPromotionActivatedEnqueueIntegrationEvent(
    IOutboxWriter<PromotionsDbContext> outbox)
    : INotificationHandler<PromotionActivatedEvent>
{
    public Task Handle(PromotionActivatedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new PromotionActivatedIntegrationEvent(
            Guid.NewGuid(),
            notification.PromotionId,
            notification.Code,
            notification.StartsAtUtc,
            notification.EndsAtUtc,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

/// <summary>
/// Paused and closed both publish <c>PromotionClosedIntegrationEvent</c>, with the reason — a
/// consumer's question is "can customers still use this", and the answer is no in both cases.
/// The distinction between pausing and closing is internal campaign management.
/// </summary>
internal sealed class OnPromotionPausedEnqueueIntegrationEvent(
    IOutboxWriter<PromotionsDbContext> outbox)
    : INotificationHandler<PromotionPausedEvent>
{
    public Task Handle(PromotionPausedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new PromotionClosedIntegrationEvent(
            Guid.NewGuid(),
            notification.PromotionId,
            notification.Code,
            nameof(PromotionStatusValue.Paused),
            RedemptionCount: 0,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnPromotionClosedEnqueueIntegrationEvent(
    IOutboxWriter<PromotionsDbContext> outbox)
    : INotificationHandler<PromotionClosedEvent>
{
    public Task Handle(PromotionClosedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new PromotionClosedIntegrationEvent(
            Guid.NewGuid(),
            notification.PromotionId,
            notification.Code,
            notification.Reason.ToString(),
            notification.RedemptionCount,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnPromotionRedeemedEnqueueIntegrationEvent(
    IOutboxWriter<PromotionsDbContext> outbox)
    : INotificationHandler<PromotionRedeemedEvent>
{
    public Task Handle(PromotionRedeemedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new PromotionRedeemedIntegrationEvent(
            Guid.NewGuid(),
            notification.PromotionId,
            notification.Code,
            notification.CustomerId,
            notification.CheckoutSessionId,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnPromotionReleasedEnqueueIntegrationEvent(
    IOutboxWriter<PromotionsDbContext> outbox)
    : INotificationHandler<PromotionReleasedEvent>
{
    public Task Handle(PromotionReleasedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new PromotionReleasedIntegrationEvent(
            Guid.NewGuid(),
            notification.PromotionId,
            notification.Code,
            notification.CustomerId,
            notification.CheckoutSessionId,
            notification.Reason,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}
