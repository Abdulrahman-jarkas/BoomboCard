using BoomboCard.Loyalty.Contracts.IntegrationEvents;
using BoomboCard.Loyalty.Data;
using BoomboCard.Loyalty.Domain.Ledger;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate.Events;
using BoomboCard.SharedKernel.Persistence.Outbox;
using MediatR;

namespace BoomboCard.Loyalty.Application.Points.IntegrationEvents;

/// <summary>
/// The bridge from this module's internal <c>PointsMovedEvent</c> to its published contracts.
///
/// <para>
/// One internal event covers every movement, and this translator fans it out to the three
/// contracts consumers actually care about — earned, redeemed, expired. The other reasons
/// (returned, reversed, adjusted, forfeited) raise the internal event and publish nothing,
/// because no other module needs to know; they are still recorded, because the event is the
/// record of the fact and adding a listener later must not mean editing the aggregate.
/// </para>
///
/// <para>
/// No I/O and no call into another context — this runs inside the transaction, so the outbox
/// row commits with the ledger entry that produced it.
/// </para>
/// </summary>
internal sealed class OnPointsMovedEnqueueIntegrationEvent(IOutboxWriter<LoyaltyDbContext> outbox)
    : INotificationHandler<PointsMovedEvent>
{
    public Task Handle(PointsMovedEvent notification, CancellationToken cancellationToken)
    {
        switch (notification.Reason)
        {
            case PointsReason.Earned:
                outbox.Enqueue(new PointsEarnedIntegrationEvent(
                    Guid.NewGuid(),
                    notification.CustomerId,
                    notification.Delta,
                    notification.Reference,
                    notification.OccurredAtUtc));
                break;

            case PointsReason.Redeemed:
                // Points only, no money. The internal event does not know the basket's currency,
                // and the only way to populate a cash figure here would be to guess one — see
                // PointsRedeemedIntegrationEvent for why that field was removed rather than
                // filled in.
                outbox.Enqueue(new PointsRedeemedIntegrationEvent(
                    Guid.NewGuid(),
                    notification.CustomerId,
                    -notification.Delta,
                    notification.Reference,
                    notification.OccurredAtUtc));
                break;

            case PointsReason.Expired:
                outbox.Enqueue(new PointsExpiredIntegrationEvent(
                    Guid.NewGuid(),
                    notification.CustomerId,
                    -notification.Delta,
                    notification.OccurredAtUtc));
                break;
        }

        return Task.CompletedTask;
    }
}
