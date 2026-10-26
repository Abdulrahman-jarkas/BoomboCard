using BoomboCard.Customers.Contracts.IntegrationEvents;
using BoomboCard.Loyalty.Contracts.IntegrationEvents;
using BoomboCard.Notifications.Application.Notifications.Commands;
using BoomboCard.Notifications.Data;
using BoomboCard.Notifications.Domain.NotificationAggregate;
using BoomboCard.Notifications.Domain.Templates;
using BoomboCard.Payments.Contracts.IntegrationEvents;
using BoomboCard.Sales.Contracts.IntegrationEvents;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Inbox;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Notifications.Application.IntegrationEvents;

/*
 * Eleven consumers across four modules, and every one of them is the same nine lines.
 *
 * That uniformity is the point. All the thinking — idempotency on (Kind, Reference), recipient
 * resolution, rendering, suppression when there is nowhere to send — lives in
 * EnqueueNotificationCommand, so a consumer does nothing but name the kind, name the reference,
 * and fill in the facts. Adding the twelfth is a nine-line file, which is the test of whether the
 * seam was drawn in the right place.
 *
 * Every one follows the three-step inbox shape:
 *
 *   1. Claim the event for this consumer, or return. The claim is ADDED, not saved, so it commits
 *      with the notification row.
 *   2. Enqueue.
 *   3. One SaveAndDispatchAsync, which writes the claim and the notification together.
 *
 * Step 3 is SaveAndDispatchAsync rather than SaveChangesAsync because an integration event
 * handler is not dispatched through the MediatR request pipeline — nothing has opened a
 * transaction and nothing would drain the domain events. That is BUGS-AND-LESSONS §10, and the
 * guard in SaveChangesAsync now throws rather than letting it pass silently.
 *
 * THE RULE THEY ALL SHARE: a notification failure never fails the business operation. If
 * enqueueing cannot work, the handler logs it and saves the claim anyway. An order that cannot be
 * emailed about is still an order; refusing to acknowledge the event would park it in the outbox
 * forever and block every message behind it.
 */

/// <summary>
/// The shared body of every consumer in this file.
///
/// <para>
/// Extracted rather than repeated eleven times, which also means the "a notification failure
/// never fails the business operation" rule is written once and cannot be forgotten by the
/// twelfth consumer.
/// </para>
/// </summary>
internal static class NotificationConsumer
{
    public static async Task EnqueueAsync(
        IInboxGuard<NotificationsDbContext> inbox,
        IUnitOfWork<NotificationsDbContext> unitOfWork,
        ISender sender,
        ILogger logger,
        IIntegrationEvent notification,
        Type consumer,
        EnqueueNotificationCommand command,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, consumer, cancellationToken))
        {
            return;
        }

        // ISender, not ICommandDispatcher: the command runs through this module's own pipeline
        // behaviour, which joins the transaction this handler's save will commit — so the
        // notification row and the inbox claim land together. The retrying dispatcher would open
        // a NEW scope with a new context, and the claim would then be in a different transaction
        // from the work it is supposed to guard.
        var enqueued = await sender.Send(command, cancellationToken);

        if (enqueued.IsError)
        {
            // Logged, never thrown. See the rule at the top of this file.
            logger.LogError(
                "Could not queue a {Kind} notification for {Reference}: {Error}. The event is "
                + "acknowledged anyway — a notification failure must not block the outbox.",
                command.Kind,
                command.Reference,
                enqueued.FirstError.Description);
        }
        else if (enqueued.Value.AlreadyQueued)
        {
            // A redelivery, which is expected rather than exceptional. Not worth a warning.
            logger.LogDebug(
                "A {Kind} notification for {Reference} already exists; nothing queued.",
                command.Kind,
                command.Reference);
        }

        await unitOfWork.SaveAndDispatchAsync(cancellationToken);
    }
}

// ----------------------------------------------------------------- Customers

/// <summary>
/// The verification email. <b>Phase 1 built the token and could not send it.</b>
///
/// <para>
/// <c>ISSUES.md</c> §7 lists "email verification" as a capability Onyx did not have. BoomboCard
/// has had the token, the expiry, the single-use marker and the redemption endpoint since Phase 1
/// — and until now, nothing that put the link in front of a customer. The account could not be
/// verified, so it could not check out.
/// </para>
///
/// <para>
/// The event carries the plaintext token, because it has to: the database stores a hash, so after
/// the event is published nobody — this system included — can recover it. Which means the
/// rendered body is a credential, and is treated as one: never logged by the sender, never
/// exposed by a query, and discarded by the retention job once the message is delivered.
/// </para>
/// </summary>
internal sealed class OnEmailVerificationRequestedQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    NotificationLinkBuilder links,
    ILogger<OnEmailVerificationRequestedQueueNotification> logger)
    : INotificationHandler<EmailVerificationRequestedIntegrationEvent>
{
    public Task Handle(
        EmailVerificationRequestedIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.EmailVerification,
                // The TOKEN, not the customer id. A customer may legitimately request
                // verification more than once — a link that expired, an address they mistyped —
                // and each request is a different message with a different link. Keying on the
                // customer would send the first link and silently swallow every later one.
                $"verify:{notification.Token}",
                notification.CustomerId,
                notification.Email,
                new NotificationFacts
                {
                    Link = links.EmailVerification(notification.Token),
                    ExpiresAtUtc = notification.ExpiresAtUtc
                }),
            cancellationToken);
}

/// <summary>
/// The reset link. Keyed on the token for the same reason as verification: asking twice is
/// normal, and the second request must not be swallowed.
/// </summary>
internal sealed class OnPasswordResetRequestedQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    NotificationLinkBuilder links,
    ILogger<OnPasswordResetRequestedQueueNotification> logger)
    : INotificationHandler<PasswordResetRequestedIntegrationEvent>
{
    public Task Handle(
        PasswordResetRequestedIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.PasswordReset,
                $"reset:{notification.Token}",
                notification.CustomerId,
                notification.Email,
                new NotificationFacts
                {
                    Link = links.PasswordReset(notification.Token),
                    ExpiresAtUtc = notification.ExpiresAtUtc
                }),
            cancellationToken);
}

/// <summary>
/// "Your password was changed."
///
/// <para>
/// Sent whether or not the owner made the change, because <b>the case that matters is the one
/// where they did not</b>. This is how a successful credential-stuffing attempt reaches the only
/// person able to act on it — and the event's own documentation in Customers.Contracts has said
/// so since Phase 1, waiting for a consumer.
/// </para>
/// </summary>
internal sealed class OnPasswordChangedQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    ILogger<OnPasswordChangedQueueNotification> logger)
    : INotificationHandler<CustomerPasswordChangedIntegrationEvent>
{
    public Task Handle(
        CustomerPasswordChangedIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.PasswordChanged,
                // The event id, so every change produces a message. A customer who changes their
                // password three times should be told three times — keying on the customer would
                // tell them once and then go quiet, which is exactly wrong for a security notice.
                $"password-changed:{notification.EventId}",
                notification.CustomerId,
                notification.Email,
                new NotificationFacts
                {
                    OccurredAtUtc = notification.OccurredAtUtc,
                    ViaReset = notification.ViaReset
                }),
            cancellationToken);
}

// ----------------------------------------------------------------- Sales

/// <summary>
/// The order confirmation. <b>Onyx's <c>SendConfirmationEmail</c>, which threw
/// <c>NotImplementedException</c> into a discarded task.</b>
///
/// <para>
/// Note what it says and does not say: the order exists, payment is <i>pending</i>. Onyx
/// <b>A9</b> told the customer their payment had succeeded before the payment row was added to
/// the context — so a rolled-back transaction left them holding a confirmation for a transaction
/// that never happened. "Payment received" is a separate message, sent when a provider says the
/// money moved.
/// </para>
/// </summary>
internal sealed class OnOrderPlacedQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    ILogger<OnOrderPlacedQueueNotification> logger)
    : INotificationHandler<OrderPlacedIntegrationEvent>
{
    public Task Handle(
        OrderPlacedIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.OrderPlaced,
                notification.OrderId.ToString(),
                notification.CustomerId,
                Address: null,
                new NotificationFacts
                {
                    OrderNumber = notification.OrderNumber,
                    Amount = notification.Total,
                    CurrencyCode = notification.CurrencyCode,
                    OccurredAtUtc = notification.OccurredAtUtc
                }),
            cancellationToken);
}

/// <summary>
/// "It is on its way." The single most-expected message in retail, and Onyx <b>A31</b> is that
/// nothing in that codebase ever set <c>Shipped</c> at all.
/// </summary>
internal sealed class OnOrderShippedQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    ILogger<OnOrderShippedQueueNotification> logger)
    : INotificationHandler<OrderShippedIntegrationEvent>
{
    public Task Handle(
        OrderShippedIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.OrderShipped,
                // The event id, not the order id: partial shipment means one order ships more
                // than once, and each parcel is its own message. Keying on the order would tell
                // the customer about the first box and never mention the second.
                $"shipped:{notification.EventId}",
                notification.CustomerId,
                Address: null,
                new NotificationFacts
                {
                    OrderNumber = notification.OrderNumber,
                    ItemCount = notification.ItemsShipped,
                    PartialShipment = notification.PartialShipment,
                    OccurredAtUtc = notification.OccurredAtUtc
                }),
            cancellationToken);
}

internal sealed class OnOrderDeliveredQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    ILogger<OnOrderDeliveredQueueNotification> logger)
    : INotificationHandler<OrderDeliveredIntegrationEvent>
{
    public Task Handle(
        OrderDeliveredIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.OrderDelivered,
                notification.OrderId.ToString(),
                notification.CustomerId,
                Address: null,
                new NotificationFacts
                {
                    OrderNumber = notification.OrderNumber,
                    OccurredAtUtc = notification.OccurredAtUtc
                }),
            cancellationToken);
}

internal sealed class OnOrderCancelledQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    ILogger<OnOrderCancelledQueueNotification> logger)
    : INotificationHandler<OrderCancelledIntegrationEvent>
{
    public Task Handle(
        OrderCancelledIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.OrderCancelled,
                notification.OrderId.ToString(),
                notification.CustomerId,
                Address: null,
                new NotificationFacts
                {
                    OrderNumber = notification.OrderNumber,
                    Reason = notification.Reason,
                    OccurredAtUtc = notification.OccurredAtUtc
                }),
            cancellationToken);
}

// ----------------------------------------------------------------- Payments

/// <summary>
/// "Payment received." <b>The message Onyx sent first and could not justify.</b>
///
/// <para>
/// <c>NotifyPaymentConfirmed</c> was dispatched before the payment row was added to the context,
/// before <c>SaveChanges</c> and before <c>Commit</c> — and its body threw
/// <c>NotImplementedException</c> into a discarded task, so it never ran anyway. Both halves of
/// <b>A8</b> and <b>A9</b> in one line.
/// </para>
///
/// <para>
/// Here it is queued inside the transaction that consumed <c>PaymentCaptured</c>, which is
/// published by the outbox only after the capture committed. The ordering cannot be got wrong by
/// accident.
/// </para>
/// </summary>
internal sealed class OnPaymentCapturedQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    ILogger<OnPaymentCapturedQueueNotification> logger)
    : INotificationHandler<PaymentCapturedIntegrationEvent>
{
    public Task Handle(
        PaymentCapturedIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.PaymentReceived,
                notification.PaymentId.ToString(),
                notification.CustomerId,
                Address: null,
                new NotificationFacts
                {
                    Amount = notification.Amount,
                    CurrencyCode = notification.CurrencyCode,
                    Reference = notification.ProviderReference,
                    OccurredAtUtc = notification.OccurredAtUtc
                }),
            cancellationToken);
}

/// <summary>
/// "We could not take payment." The one notification with an instruction in it.
///
/// <para>
/// Subscribes to <c>PaymentDeclined</c> and <b>not</b> to <c>PaymentFailed</c>, deliberately. A
/// decline is the provider's considered answer and the customer must act on it. A failure may be
/// a transport problem on our side, and three of them may be followed by a successful fourth
/// attempt — emailing the customer about each one would be telling them about our infrastructure.
/// And a failure with an unknown outcome may mean the charge <i>succeeded</i>, in which case
/// "your payment failed" is simply false.
/// </para>
/// </summary>
internal sealed class OnPaymentDeclinedQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    ILogger<OnPaymentDeclinedQueueNotification> logger)
    : INotificationHandler<PaymentDeclinedIntegrationEvent>
{
    public Task Handle(
        PaymentDeclinedIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.PaymentDeclined,
                notification.PaymentId.ToString(),
                notification.CustomerId,
                Address: null,
                new NotificationFacts
                {
                    OrderNumber = notification.OrderId.ToString(),
                    DeclineCode = notification.DeclineCode,
                    OccurredAtUtc = notification.OccurredAtUtc
                }),
            cancellationToken);
}

internal sealed class OnPaymentRefundedQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    ILogger<OnPaymentRefundedQueueNotification> logger)
    : INotificationHandler<PaymentRefundedIntegrationEvent>
{
    public Task Handle(
        PaymentRefundedIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.RefundIssued,
                // The event id: a partial refund can happen several times against one payment,
                // and each one moves real money and deserves its own message.
                $"refund:{notification.EventId}",
                notification.CustomerId,
                Address: null,
                new NotificationFacts
                {
                    OrderNumber = notification.OrderId.ToString(),
                    Amount = notification.Amount,
                    CurrencyCode = notification.CurrencyCode,
                    Reference = notification.ProviderReference,
                    OccurredAtUtc = notification.OccurredAtUtc
                }),
            cancellationToken);
}

// ----------------------------------------------------------------- Loyalty

/// <summary>
/// "You earned points."
///
/// <para>
/// Onyx <b>M11</b> is that its points were worth nothing and <b>M12</b> that they could never be
/// earned. BoomboCard fixed both in Phase 3 and 4 — and a reward nobody is told about is still
/// close to worthless, which is what this closes.
/// </para>
/// </summary>
internal sealed class OnPointsEarnedQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    ILogger<OnPointsEarnedQueueNotification> logger)
    : INotificationHandler<PointsEarnedIntegrationEvent>
{
    public Task Handle(
        PointsEarnedIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.PointsEarned,
                $"points-earned:{notification.EventId}",
                notification.CustomerId,
                Address: null,
                new NotificationFacts
                {
                    Points = notification.Points,
                    OrderNumber = notification.OrderReference,
                    OccurredAtUtc = notification.OccurredAtUtc
                }),
            cancellationToken);
}

internal sealed class OnPointsExpiredQueueNotification(
    IInboxGuard<NotificationsDbContext> inbox,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    ILogger<OnPointsExpiredQueueNotification> logger)
    : INotificationHandler<PointsExpiredIntegrationEvent>
{
    public Task Handle(
        PointsExpiredIntegrationEvent notification,
        CancellationToken cancellationToken) =>
        NotificationConsumer.EnqueueAsync(
            inbox, unitOfWork, sender, logger, notification, GetType(),
            new EnqueueNotificationCommand(
                NotificationKind.PointsExpired,
                $"points-expired:{notification.EventId}",
                notification.CustomerId,
                Address: null,
                new NotificationFacts
                {
                    Points = notification.Points,
                    OccurredAtUtc = notification.OccurredAtUtc
                }),
            cancellationToken);
}
