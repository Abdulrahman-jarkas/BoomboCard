using BoomboCard.Catalog.Contracts;
using BoomboCard.Loyalty.Contracts;
using BoomboCard.Promotions.Contracts;
using BoomboCard.Sales.Contracts.IntegrationEvents;
using BoomboCard.Sales.Data;
using BoomboCard.Sales.Data.Repositories;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Inbox;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Sales.Application.IntegrationEvents;

/// <summary>
/// Puts back everything a dead checkout or a cancelled order was holding. <b>Recovery layer
/// 2.</b>
///
/// <para>
/// <b>Why this lives in Sales rather than three handlers in three modules.</b> Either shape
/// works, and this one is better for a reason worth stating: Sales is the only module that knows
/// what a checkout <i>held</i> — which lines, whether a promotion was applied, how many points.
/// Putting a <c>CheckoutAbandoned</c> subscriber in Catalog would mean Catalog referencing
/// <c>Sales.Contracts</c> and then asking Sales what the lines were, or Sales publishing the
/// lines on the event so Catalog can read them. Both make three modules know about the saga.
/// </para>
///
/// <para>
/// This way the dependency arrows all point out of Sales — Catalog, Promotions and Loyalty stay
/// completely ignorant that Sales exists, which is the strongest version of the boundary. Sales
/// is the process manager; the others just answer commands.
/// </para>
///
/// <para>
/// <b>Why an event at all, rather than releasing inline in the expiry job.</b> The outbox is
/// what makes the release <i>guaranteed</i>: the row commits with the session's expiry, and the
/// dispatcher retries until every consumer succeeds, with a dead-letter if one never does.
/// Releasing inline would make the guarantee only as good as the job's next five seconds.
/// </para>
///
/// <para>
/// Every release command is idempotent on the session reference and treats "nothing was held" as
/// success — <c>RestoreStockCommand</c>, <c>ReleasePromotionCommand</c> and
/// <c>ReleasePointsCommand</c> all do. That is what lets this fire unconditionally without first
/// asking whether there is anything to undo, and it is why layer 2 is the layer the saga
/// actually relies on while layer 1 is only an optimisation.
/// </para>
/// </summary>
internal sealed class OnCheckoutAbandonedReleaseHolds(
    ISalesRepository sales,
    IUnitOfWork<SalesDbContext> unitOfWork,
    IInboxGuard<SalesDbContext> inbox,
    ICommandDispatcher dispatcher,
    ILogger<OnCheckoutAbandonedReleaseHolds> logger)
    : INotificationHandler<CheckoutAbandonedIntegrationEvent>
{
    public async Task Handle(
        CheckoutAbandonedIntegrationEvent notification,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, GetType(), cancellationToken))
        {
            return;
        }

        var session = await sales.GetSessionAsync(notification.CheckoutSessionId, cancellationToken);

        // The claim is saved either way, so a session that has vanished does not leave the
        // message being retried forever.
        if (session is null)
        {
            logger.LogError(
                "Checkout {SessionId} was abandoned but no session row exists. Nothing can be "
                + "released, because the lines are unknown.",
                notification.CheckoutSessionId);

            await unitOfWork.SaveAndDispatchAsync(cancellationToken);
            return;
        }

        // Nothing was ever held, so there is nothing to release.
        //
        // This check is the difference between releasing what TX 1 took and releasing what the
        // basket CONTAINED. Without it a customer who abandoned an Open checkout had its
        // contents added to stock — inventory created out of a cancelled basket, which is Onyx
        // D5's shape in our own code. Catalog now clamps to what it actually holds as well, but
        // asking for a release that cannot be right is still wrong, and the log stays quiet when
        // this returns early. BUGS-AND-LESSONS section 13.
        if (!session.EverHeldAnything)
        {
            logger.LogInformation(
                "Checkout {SessionId} was abandoned before anything was held ({Reason}). "
                + "Nothing to release.",
                notification.CheckoutSessionId,
                notification.Reason);

            await unitOfWork.SaveAndDispatchAsync(cancellationToken);
            return;
        }

        var reference = session.HoldReference;

        await ReleaseEverything.ForAsync(
            dispatcher,
            logger,
            reference,
            session.Lines.Select(line => new StockLine(line.ProductId, line.Quantity)).ToList(),
            session.PromotionId is not null,
            session.PointsRedeemed > 0,
            RestoreReason.SessionExpired,
            ReleaseReason.SessionExpired,
            PointsReleaseReason.SessionExpired,
            cancellationToken);

        // SaveAndDispatchAsync, not SaveChangesAsync: an integration event handler is not in the
        // MediatR request pipeline, so nothing has opened a transaction and nothing would drain
        // events. See bug 10 — and the guard that now makes the wrong call throw.
        await unitOfWork.SaveAndDispatchAsync(cancellationToken);

        logger.LogInformation(
            "Released the holds for abandoned checkout {SessionId} ({Reason}).",
            notification.CheckoutSessionId,
            notification.Reason);
    }
}

/// <summary>
/// The same release, driven by a cancelled order rather than a dead checkout.
///
/// <para>
/// Separate handler, same mechanism: the order carries the session id, every hold was placed
/// under it, and the release commands are idempotent — so an order cancelled after its session
/// already expired releases nothing a second time.
/// </para>
/// </summary>
internal sealed class OnOrderCancelledReleaseHolds(
    ISalesRepository sales,
    IUnitOfWork<SalesDbContext> unitOfWork,
    IInboxGuard<SalesDbContext> inbox,
    ICommandDispatcher dispatcher,
    ILogger<OnOrderCancelledReleaseHolds> logger)
    : INotificationHandler<OrderCancelledIntegrationEvent>
{
    public async Task Handle(
        OrderCancelledIntegrationEvent notification,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, GetType(), cancellationToken))
        {
            return;
        }

        var order = await sales.GetOrderAsync(notification.OrderId, cancellationToken);

        if (order is null)
        {
            logger.LogError(
                "Order {OrderId} was cancelled but no order row exists.", notification.OrderId);

            await unitOfWork.SaveAndDispatchAsync(cancellationToken);
            return;
        }

        await ReleaseEverything.ForAsync(
            dispatcher,
            logger,
            notification.CheckoutSessionId.ToString(),
            order.Items.Select(item => new StockLine(item.ProductId, item.Quantity)).ToList(),
            order.PromotionId is not null,
            order.PointsRedeemed > 0,
            RestoreReason.OrderCancelled,
            ReleaseReason.OrderCancelled,
            PointsReleaseReason.OrderCancelled,
            cancellationToken);

        await unitOfWork.SaveAndDispatchAsync(cancellationToken);

        logger.LogInformation(
            "Released the holds for cancelled order {OrderNumber}.", notification.OrderNumber);
    }
}

/// <summary>
/// The three release calls, in one place, with per-call error handling.
///
/// <para>
/// Each is attempted independently and a failure is logged rather than thrown. Throwing would
/// fail the whole outbox message and retry all three — including the ones that succeeded. They
/// are idempotent, so that would be safe, but it would also mean one permanently broken release
/// blocks the other two forever. Per-call handling is the same reasoning
/// <c>OutboxDispatcher</c> applies per message: RiverBooks' poller never marks a failure, so one
/// bad message blocks every message behind it.
/// </para>
/// </summary>
internal static class ReleaseEverything
{
    public static async Task ForAsync(
        ICommandDispatcher dispatcher,
        ILogger logger,
        string reference,
        IReadOnlyList<StockLine> lines,
        bool hadPromotion,
        bool hadPoints,
        RestoreReason stockReason,
        ReleaseReason promotionReason,
        PointsReleaseReason pointsReason,
        CancellationToken cancellationToken)
    {
        await TryAsync(
            () => dispatcher.SendAsync(
                new RestoreStockCommand(reference, stockReason, lines), cancellationToken),
            "restore stock");

        if (hadPromotion)
        {
            await TryAsync(
                () => dispatcher.SendAsync(
                    new ReleasePromotionCommand(reference, promotionReason), cancellationToken),
                "release the promotion");
        }

        if (hadPoints)
        {
            await TryAsync(
                () => dispatcher.SendAsync(
                    new ReleasePointsCommand(reference, pointsReason), cancellationToken),
                "return the points");
        }

        async Task TryAsync(Func<Task> step, string what)
        {
            try
            {
                await step();
            }
            catch (Exception exception)
            {
                // Logged with full context, never swallowed silently — Onyx A16's empty catch
                // blocks destroyed all diagnostic information about every gateway failure.
                logger.LogError(
                    exception,
                    "Could not {What} for reference {Reference}. The orphan reconciliation sweep "
                    + "is the backstop.",
                    what,
                    reference);
            }
        }
    }
}
