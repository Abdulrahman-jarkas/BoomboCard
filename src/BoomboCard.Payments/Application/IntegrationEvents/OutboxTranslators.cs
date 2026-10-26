using BoomboCard.Payments.Contracts.IntegrationEvents;
using BoomboCard.Payments.Data;
using BoomboCard.Payments.Domain.PaymentAggregate.Events;
using BoomboCard.SharedKernel.Persistence.Outbox;
using MediatR;

namespace BoomboCard.Payments.Application.IntegrationEvents;

/*
 * The bridge from Payments' internal events to its published contracts.
 *
 * No I/O and no call into another context — these run inside the command's transaction, so the
 * outbox row commits with the state change that produced it.
 *
 * Onyx A9 is the finding this arrangement exists for, and it is worth being precise about:
 * its controller dispatched the payment notification BEFORE the payment row was even added to
 * the context. A customer could be told their payment had succeeded for a transaction that then
 * rolled back. Here there is no state in which an event exists and its cause does not, because
 * both are rows in the same SaveChanges.
 *
 * Note which internal events have NO translator: PaymentInitiatedEvent and
 * PaymentAuthorizedEvent. Nobody outside Payments needs to know an intent exists or that funds
 * are merely reserved — publishing them would be advertising a fact no consumer can act on, and
 * every published contract is a coupling that has to be maintained forever.
 */

internal sealed class OnPaymentCapturedEnqueueIntegrationEvent(
    IOutboxWriter<PaymentsDbContext> outbox)
    : INotificationHandler<PaymentCapturedEvent>
{
    public Task Handle(PaymentCapturedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new PaymentCapturedIntegrationEvent(
            Guid.NewGuid(),
            notification.PaymentId,
            notification.OrderId,
            notification.CustomerId,
            notification.ProviderReference,
            notification.Amount,
            notification.CurrencyCode,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnPaymentDeclinedEnqueueIntegrationEvent(
    IOutboxWriter<PaymentsDbContext> outbox)
    : INotificationHandler<PaymentDeclinedEvent>
{
    public Task Handle(PaymentDeclinedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new PaymentDeclinedIntegrationEvent(
            Guid.NewGuid(),
            notification.PaymentId,
            notification.OrderId,
            notification.CustomerId,
            notification.CheckoutSessionId,
            notification.DeclineCode,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

/// <summary>
/// Published on every failure, not only the final one — a consumer may want to tell the customer
/// we are retrying.
///
/// <para>
/// <c>OutcomeIsUnknown</c> and <c>AttemptsExhausted</c> both travel, because a consumer deciding
/// whether to release an order's holds needs both: only an exhausted budget with a <i>known</i>
/// failure is safe to compensate for.
/// </para>
/// </summary>
internal sealed class OnPaymentFailedEnqueueIntegrationEvent(
    IOutboxWriter<PaymentsDbContext> outbox)
    : INotificationHandler<PaymentFailedEvent>
{
    public Task Handle(PaymentFailedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new PaymentFailedIntegrationEvent(
            Guid.NewGuid(),
            notification.PaymentId,
            notification.OrderId,
            notification.CustomerId,
            notification.CheckoutSessionId,
            notification.OutcomeIsUnknown,
            notification.AttemptsExhausted,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnPaymentRefundedEnqueueIntegrationEvent(
    IOutboxWriter<PaymentsDbContext> outbox)
    : INotificationHandler<PaymentRefundedEvent>
{
    public Task Handle(PaymentRefundedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new PaymentRefundedIntegrationEvent(
            Guid.NewGuid(),
            notification.PaymentId,
            notification.OrderId,
            notification.CustomerId,
            notification.Amount,
            notification.CurrencyCode,
            notification.Fully,
            notification.ProviderReference,
            notification.Reason,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnPaymentChargedBackEnqueueIntegrationEvent(
    IOutboxWriter<PaymentsDbContext> outbox)
    : INotificationHandler<PaymentChargedBackEvent>
{
    public Task Handle(PaymentChargedBackEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new PaymentChargedBackIntegrationEvent(
            Guid.NewGuid(),
            notification.PaymentId,
            notification.OrderId,
            notification.CustomerId,
            notification.Amount,
            notification.CurrencyCode,
            notification.DisputeReference,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}
