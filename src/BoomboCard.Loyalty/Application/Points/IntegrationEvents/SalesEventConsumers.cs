using BoomboCard.Loyalty.Contracts;
using BoomboCard.Loyalty.Data;
using BoomboCard.Sales.Contracts.IntegrationEvents;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Inbox;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Loyalty.Application.Points.IntegrationEvents;

/// <summary>
/// Credits the points an order earned. <b>The trigger Phase 3 built the earn rule for and could
/// not wire.</b>
///
/// <para>
/// Loyalty shipped with <c>EarnRule</c>, the ledger, the idempotency index and
/// <c>GrantPointsCommand</c> fully tested, and nothing that called them — because a module
/// cannot subscribe to a contract that has not been written, and Sales did not exist. The gap
/// was recorded in <c>ROADMAP.md</c> and in <c>GrantPointsCommand</c>'s own documentation rather
/// than implied, and this handler is the whole of closing it: <b>nine lines of work and no new
/// rule</b>. That the earn side needed nothing but a subscription is the clearest evidence the
/// seam was drawn in the right place.
/// </para>
///
/// <para>
/// Onyx <b>M12</b>: the points balance had a spend side and no earn side — nothing anywhere
/// increased it, so the scheme could only ever drain. It is closed here.
/// </para>
///
/// <para>
/// <b>Why paid and not placed.</b> Points are a reward for money received. Earning on an order
/// whose card later declines pays out for a sale that never happened, and the reversal would
/// then have to claw back points the customer may already have spent. <c>OrderPaid</c> is the
/// earliest event at which the money is real.
/// </para>
///
/// <para>
/// Two guards, as everywhere: the inbox claim catches the ordinary redelivery, and
/// <c>UX_PointsLedger_Idempotency</c> on <c>(AccountId, Reference, Reason)</c> catches two
/// deliveries racing past the claim's read. <c>GrantPointsCommand</c> is idempotent on the order
/// reference, so a redelivery credits once even if both guards were somehow bypassed.
/// </para>
/// </summary>
internal sealed class OnOrderPaidGrantPoints(
    IUnitOfWork<LoyaltyDbContext> unitOfWork,
    IInboxGuard<LoyaltyDbContext> inbox,
    ISender sender,
    ILogger<OnOrderPaidGrantPoints> logger)
    : INotificationHandler<OrderPaidIntegrationEvent>
{
    public async Task Handle(
        OrderPaidIntegrationEvent notification,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, GetType(), cancellationToken))
        {
            return;
        }

        // ISender, not ICommandDispatcher: the command runs through Loyalty's own pipeline
        // behaviour, which joins the transaction this handler's save will commit — so the grant,
        // its ledger row and the inbox claim land together. The retrying dispatcher would open a
        // NEW scope with a new context, and the claim would then be in a different transaction
        // from the work it is supposed to guard.
        var granted = await sender.Send(
            new GrantPointsCommand(
                notification.CustomerId,
                // Merchandise after discounts, excluding tax and shipping. Sales computed it
                // once at the sale and carries it on the order — Loyalty does not re-derive it,
                // because two derivations of the same figure is how they come to disagree.
                notification.QualifyingSpend,
                notification.CurrencyCode,
                notification.OrderId.ToString()),
            cancellationToken);

        if (granted.IsError)
        {
            // Logged, not thrown. A grant that fails for a business reason — an unsupported
            // currency, say — will fail identically on every retry, and throwing would park the
            // message in the outbox forever. The claim is saved, so this is a decision not to
            // credit, recorded. Onyx A16 swallowed these into empty catch blocks with no log at
            // all, which is how its payment failures became invisible.
            logger.LogError(
                "Could not credit the points for order {OrderNumber}: {Error}",
                notification.OrderNumber,
                granted.FirstError.Description);
        }
        else
        {
            logger.LogInformation(
                "Credited {Points} points to customer {CustomerId} for order {OrderNumber}.",
                granted.Value,
                notification.CustomerId,
                notification.OrderNumber);
        }

        await unitOfWork.SaveAndDispatchAsync(cancellationToken);
    }
}

/// <summary>
/// Reverses the points an order earned, because that order was refunded.
///
/// <para>
/// The other half of <b>M12</b>'s obligation line — "an explicit earn rule … with reversal on
/// refund". The reversal is bounded by what the earning actually gave and clamped to the current
/// balance, both decided by <c>PointsAccount.ReverseEarning</c> rather than here: a customer who
/// earned 500 and spent 400 has 100 left, and a refund cannot claw back what is already gone
/// without a negative balance, which this scheme does not represent.
/// </para>
///
/// <para>
/// <b>Why this one subscribes directly while the release fan-out lives in Sales.</b> A release
/// needs to know what the checkout held — which lines, how many points — and only Sales can say,
/// so Sales orchestrates it as a process manager. A reversal needs nothing but the customer and
/// the order, both carried on this event, and Loyalty's own ledger holds the rest. Where a
/// consumer can act on the event alone it subscribes to the event alone; the asymmetry tracks
/// who owns the missing information rather than taste.
/// </para>
/// </summary>
internal sealed class OnOrderRefundedReverseEarnedPoints(
    IUnitOfWork<LoyaltyDbContext> unitOfWork,
    IInboxGuard<LoyaltyDbContext> inbox,
    ISender sender,
    ILogger<OnOrderRefundedReverseEarnedPoints> logger)
    : INotificationHandler<OrderRefundedIntegrationEvent>
{
    public async Task Handle(
        OrderRefundedIntegrationEvent notification,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, GetType(), cancellationToken))
        {
            return;
        }

        var reversed = await sender.Send(
            new ReverseEarnedPointsCommand(
                notification.CustomerId,
                notification.OrderId.ToString(),
                notification.RefundReference),
            cancellationToken);

        if (reversed.IsError)
        {
            logger.LogError(
                "Could not reverse the points earned on refunded order {OrderNumber}: {Error}",
                notification.OrderNumber,
                reversed.FirstError.Description);
        }

        await unitOfWork.SaveAndDispatchAsync(cancellationToken);
    }
}
