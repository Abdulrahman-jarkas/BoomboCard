using BoomboCard.Payments.Contracts.IntegrationEvents;
using BoomboCard.Sales.Contracts;
using BoomboCard.Sales.Data;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Inbox;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Sales.Application.IntegrationEvents;

/// <summary>
/// Marks the order paid when the provider's money actually arrives. <b>The trigger Phase 4's
/// <c>MarkOrderPaidCommand</c> documented itself as waiting for.</b>
///
/// <para>
/// That command's own remarks said: <i>"Published as a command rather than consumed as an event
/// because Payments does not exist yet (Phase 5). When it does, the shape becomes Sales
/// subscribing to its <c>PaymentCapturedIntegrationEvent</c> through the inbox and calling
/// this."</i> This is that handler, and it is the whole of the change — the command, its
/// idempotency on the payment reference, and the state machine behind it were all already there.
/// </para>
///
/// <para>
/// <b>What this completes.</b> Capture → order paid → points earned, across three modules, none
/// of which references another's internals, each step guaranteed by an outbox row committed with
/// the state change that caused it. Onyx did all three in one controller action with four
/// separate commits and no compensation (<b>D3</b>), and set the order to <c>Paid</c> without
/// ever inspecting the gateway's answer (<b>M1</b>).
/// </para>
///
/// <para>
/// <c>MarkOrderPaidCommand</c> still exists and still has no HTTP route. That absence is
/// <b>M1</b> and <b>M4</b> enforced structurally: the only caller is this handler, and the only
/// thing that can make this handler run is a provider having said the money moved.
/// </para>
/// </summary>
internal sealed class OnPaymentCapturedMarkOrderPaid(
    IUnitOfWork<SalesDbContext> unitOfWork,
    IInboxGuard<SalesDbContext> inbox,
    ISender sender,
    ILogger<OnPaymentCapturedMarkOrderPaid> logger)
    : INotificationHandler<PaymentCapturedIntegrationEvent>
{
    public async Task Handle(
        PaymentCapturedIntegrationEvent notification,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, GetType(), cancellationToken))
        {
            return;
        }

        // ISender, not ICommandDispatcher: the command runs through Sales' own pipeline
        // behaviour, which joins the transaction this handler's save will commit — so the order,
        // its payment row and the inbox claim land together.
        //
        // The command is idempotent on the provider reference, backed by
        // UX_OrderPayments_Reference, so a redelivered capture marks the order paid once.
        var marked = await sender.Send(
            new MarkOrderPaidCommand(notification.OrderId, notification.ProviderReference),
            cancellationToken);

        if (marked.IsError)
        {
            // Logged, not thrown. A business-reason failure — an order already cancelled, say —
            // fails identically on every retry, and throwing would park the message in the outbox
            // forever. The claim is saved, so this is a decision recorded rather than lost.
            //
            // It is also worth alerting on: money has been taken for an order that will not
            // progress, which is a refund waiting to happen.
            logger.LogError(
                "Payment {PaymentId} was captured but order {OrderId} could not be marked paid: "
                + "{Error}. Money has been taken for an order that is not progressing.",
                notification.PaymentId,
                notification.OrderId,
                marked.FirstError.Description);
        }

        await unitOfWork.SaveAndDispatchAsync(cancellationToken);
    }
}

/// <summary>
/// Cancels the order when the provider definitively refuses, which releases the stock, the
/// promotion use and the points.
///
/// <para>
/// <b>This is the first thing in the system that can raise <c>RestoreReason.PaymentFailed</c></b>
/// — a value that has sat in <c>Catalog.Contracts</c> since Phase 0 with nothing able to produce
/// it, because nothing could tell a failed payment from an absent one. It is recovery layer 1 for
/// the payment step: fast, specific compensation, with the expiry sweeper still behind it if this
/// never runs.
/// </para>
///
/// <para>
/// A decline is safe to act on <b>because it is definitive</b>. The provider has answered, no
/// money moved, and no money will. Contrast the failure handler below, where the same action
/// would be an oversell.
/// </para>
/// </summary>
internal sealed class OnPaymentDeclinedCancelOrder(
    IUnitOfWork<SalesDbContext> unitOfWork,
    IInboxGuard<SalesDbContext> inbox,
    ISender sender,
    ILogger<OnPaymentDeclinedCancelOrder> logger)
    : INotificationHandler<PaymentDeclinedIntegrationEvent>
{
    public async Task Handle(
        PaymentDeclinedIntegrationEvent notification,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, GetType(), cancellationToken))
        {
            return;
        }

        var cancelled = await sender.Send(
            new CancelOrderCommand(
                notification.OrderId,
                $"payment declined ({notification.DeclineCode ?? "no code"})"),
            cancellationToken);

        if (cancelled.IsError)
        {
            logger.LogWarning(
                "Order {OrderId} could not be cancelled after its payment was declined: {Error}. "
                + "The expiry sweeper is the backstop.",
                notification.OrderId,
                cancelled.FirstError.Description);
        }
        else
        {
            logger.LogInformation(
                "Cancelled order {OrderId} after a declined payment; its holds are released by "
                + "the outbox.",
                notification.OrderId);
        }

        await unitOfWork.SaveAndDispatchAsync(cancellationToken);
    }
}

/// <summary>
/// Reacts to a payment that could not be completed — <b>carefully</b>.
///
/// <para>
/// <b>The guard in this handler is the most important line in Phase 5's consumer code.</b> It
/// cancels the order only when the attempt budget is spent <i>and</i> the outcome is known:
/// </para>
///
/// <code>
/// if (!notification.AttemptsExhausted || notification.OutcomeIsUnknown) return;
/// </code>
///
/// <para>
/// Cancelling on an <i>unknown</i> outcome would release stock for an order that may in fact have
/// been paid — an oversell caused by cleaning up, which is worse than one caused by a race
/// because it looks deliberate and the customer has a receipt. And the customer would have been
/// charged for goods that were then sold to somebody else.
/// </para>
///
/// <para>
/// So an unknown outcome is left alone for <c>payments.reconciliation</c> to resolve against the
/// provider. Onyx's handling of the same situation was to treat every unanswered request as a
/// failure and charge the card again (<b>M5</b>); the symmetric mistake on this side would be to
/// treat it as a failure and give the goods away.
/// </para>
/// </summary>
internal sealed class OnPaymentFailedCancelOrder(
    IUnitOfWork<SalesDbContext> unitOfWork,
    IInboxGuard<SalesDbContext> inbox,
    ISender sender,
    ILogger<OnPaymentFailedCancelOrder> logger)
    : INotificationHandler<PaymentFailedIntegrationEvent>
{
    public async Task Handle(
        PaymentFailedIntegrationEvent notification,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, GetType(), cancellationToken))
        {
            return;
        }

        // THE GUARD. See the class remarks.
        if (!notification.AttemptsExhausted || notification.OutcomeIsUnknown)
        {
            logger.LogInformation(
                "Payment {PaymentId} for order {OrderId} failed but is not finished with "
                + "(exhausted: {Exhausted}, outcome unknown: {Unknown}). The order is left alone.",
                notification.PaymentId,
                notification.OrderId,
                notification.AttemptsExhausted,
                notification.OutcomeIsUnknown);

            await unitOfWork.SaveAndDispatchAsync(cancellationToken);
            return;
        }

        var cancelled = await sender.Send(
            new CancelOrderCommand(notification.OrderId, "payment could not be completed"),
            cancellationToken);

        if (cancelled.IsError)
        {
            logger.LogWarning(
                "Order {OrderId} could not be cancelled after its payment failed: {Error}. The "
                + "expiry sweeper is the backstop.",
                notification.OrderId,
                cancelled.FirstError.Description);
        }

        await unitOfWork.SaveAndDispatchAsync(cancellationToken);
    }
}

/// <summary>
/// Records the refund against the order, which in turn reverses the loyalty points it earned.
///
/// <para>
/// A partial refund does not reverse the order — the goods may be partly kept — so only a full
/// one marks the order refunded. The points reversal is Loyalty's own subscription to the event
/// <c>RefundOrderCommand</c> raises, bounded by what the earning actually gave.
/// </para>
/// </summary>
internal sealed class OnPaymentRefundedRecordRefund(
    IUnitOfWork<SalesDbContext> unitOfWork,
    IInboxGuard<SalesDbContext> inbox,
    ISender sender,
    ILogger<OnPaymentRefundedRecordRefund> logger)
    : INotificationHandler<PaymentRefundedIntegrationEvent>
{
    public async Task Handle(
        PaymentRefundedIntegrationEvent notification,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, GetType(), cancellationToken))
        {
            return;
        }

        if (!notification.Fully)
        {
            logger.LogInformation(
                "Order {OrderId} was partially refunded ({Amount} {Currency}); the order stays "
                + "as it is, because the customer may be keeping some of it.",
                notification.OrderId,
                notification.Amount,
                notification.CurrencyCode);

            await unitOfWork.SaveAndDispatchAsync(cancellationToken);
            return;
        }

        var refunded = await sender.Send(
            new RefundOrderCommand(notification.OrderId, notification.ProviderReference),
            cancellationToken);

        if (refunded.IsError)
        {
            logger.LogError(
                "Order {OrderId} could not be marked refunded after payment {PaymentId} was "
                + "refunded in full: {Error}.",
                notification.OrderId,
                notification.PaymentId,
                refunded.FirstError.Description);
        }

        await unitOfWork.SaveAndDispatchAsync(cancellationToken);
    }
}
