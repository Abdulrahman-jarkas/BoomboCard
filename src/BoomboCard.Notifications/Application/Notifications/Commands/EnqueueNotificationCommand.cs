using BoomboCard.Customers.Contracts;
using BoomboCard.Notifications.Contracts;
using BoomboCard.Notifications.Data;
using BoomboCard.Notifications.Data.Repositories;
using BoomboCard.Notifications.Domain.NotificationAggregate;
using BoomboCard.Notifications.Domain.Templates;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Notifications.Application.Notifications.Commands;

/// <summary>
/// Renders a notification and queues it. <b>Contacts no provider.</b>
///
/// <para>
/// Every consumer in this module funnels through here, which is what keeps the eleven of them
/// thin: resolve the facts, call this, save. The idempotency check, the recipient resolution, the
/// rendering and the suppression decision all live in one place rather than eleven.
/// </para>
///
/// <para>
/// <b>Marked <see cref="INotificationsRequest"/>, so it runs in a transaction — and it is
/// deliberately called from inside the consumer's transaction, not after it.</b> That is
/// <b>A9</b>: Onyx dispatched its payment notification before the payment row was added to the
/// context, before <c>SaveChanges</c> and before <c>Commit</c>, so a rolled-back transaction left
/// a customer holding a confirmation for something that never happened. Here the notification row
/// and the inbox claim commit together, so a notification exists if and only if the event that
/// caused it was consumed.
/// </para>
///
/// <para>
/// The ordering is worth stating precisely, because "always post-commit" is the usual phrasing
/// and it is slightly wrong. The <i>send</i> is post-commit — a separate job, outside every
/// transaction. The <i>decision to send</i> is in-transaction, which is what makes it exactly as
/// durable as the fact it describes.
/// </para>
/// </summary>
/// <param name="Kind">What the notification is about.</param>
/// <param name="Reference">
/// What it is about, concretely — an order id, a payment id, a customer id plus a discriminator.
/// With <paramref name="Kind"/> this is the idempotency key, unique at the database.
/// </param>
/// <param name="CustomerId">
/// Whose it is. Used to resolve an address when <paramref name="Address"/> is not supplied, and
/// recorded either way.
/// </param>
/// <param name="Address">
/// The address, when the event already carried one. Customers' events do — they carry the address
/// <i>and</i> the token, because the message is the only thing that can deliver the token — so
/// those consumers pass it rather than asking a question whose answer they already have.
/// </param>
/// <param name="Facts">Everything the template needs.</param>
/// <param name="Channel">
/// How to reach them. Email today; <c>Sms</c> is declared as a seam and refuses to render,
/// so nothing can select it by accident.
/// </param>
public sealed record EnqueueNotificationCommand(
    NotificationKind Kind,
    string Reference,
    Guid? CustomerId,
    string? Address,
    NotificationFacts Facts,
    NotificationChannel Channel = NotificationChannel.Email)
    : IRequest<ErrorOr<EnqueuedNotification>>, INotificationsRequest;

/// <summary>
/// What was queued, or that nothing was.
///
/// <para>
/// <c>AlreadyQueued</c> is a success, not an error. A redelivered integration event reaching a
/// consumer that has already queued its notification has achieved its purpose; returning an error
/// would make the consumer log a failure for a system working exactly as designed.
/// </para>
/// </summary>
public sealed record EnqueuedNotification(Guid? NotificationId, bool AlreadyQueued, bool Suppressed);

internal sealed class EnqueueNotificationCommandHandler(
    INotificationRepository notifications,
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    ISender sender,
    TimeProvider timeProvider,
    ILogger<EnqueueNotificationCommandHandler> logger)
    : IRequestHandler<EnqueueNotificationCommand, ErrorOr<EnqueuedNotification>>
{
    public async Task<ErrorOr<EnqueuedNotification>> Handle(
        EnqueueNotificationCommand command,
        CancellationToken cancellationToken)
    {
        // Already queued. The read is the fast path; UX_Notifications_Idempotency is the
        // guarantee, so two consumers racing both pass this and the second insert is rejected.
        if (await notifications.ExistsAsync(command.Kind, command.Reference, cancellationToken))
        {
            return new EnqueuedNotification(null, AlreadyQueued: true, Suppressed: false);
        }

        var nowUtc = timeProvider.GetUtcNow();

        var recipient = await ResolveRecipientAsync(command, cancellationToken);

        if (recipient.IsError)
        {
            // Nowhere to send it. Recorded as suppressed rather than dropped, because "we chose
            // not to" and "we failed to" are different facts and only one is a defect — and
            // because a silently dropped notification is precisely Onyx A8.
            //
            // The common cause is an anonymised customer, which is a correct outcome: a GDPR
            // erasure should stop email, and the record of that decision is worth keeping.
            return await SuppressAsync(command, recipient.FirstError.Description, nowUtc, cancellationToken);
        }

        var rendered = NotificationTemplate.For(command.Kind, command.Channel, command.Facts);

        if (rendered.IsError)
        {
            // A template bug, not a data problem. Logged loudly because it means a kind reached
            // production with nothing to render — the A32 failure mode, a capability that exists
            // in an enum and nowhere else.
            logger.LogError(
                "No template rendered for a {Kind} notification on {Channel} ({Reference}): "
                + "{Error}. Nothing will be sent.",
                command.Kind,
                command.Channel,
                command.Reference,
                rendered.FirstError.Description);

            return rendered.Errors;
        }

        var notification = NotificationRequest.Queue(
            command.Kind,
            command.Channel,
            command.Reference,
            command.CustomerId,
            recipient.Value,
            rendered.Value,
            nowUtc);

        if (notification.IsError)
        {
            return notification.Errors;
        }

        notifications.Add(notification.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new EnqueuedNotification(notification.Value.Id, false, false);
    }

    /// <summary>
    /// Where this is going.
    ///
    /// <para>
    /// An address on the command wins, because the event that carried it is a better authority
    /// than a lookup: an email-verification message must go to the address being verified, which
    /// is not necessarily the one on the account yet.
    /// </para>
    ///
    /// <para>
    /// Otherwise Customers is asked. <c>GetCustomerSummaryQuery</c>'s own documentation has said
    /// since Phase 1 that it is "used by Sales to confirm a checkout's customer is real and may
    /// transact, and by Notifications to address a message" — this is the second half arriving.
    /// </para>
    /// </summary>
    private async Task<ErrorOr<NotificationAddress>> ResolveRecipientAsync(
        EnqueueNotificationCommand command,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(command.Address))
        {
            return NotificationAddress.Create(command.Address, command.Facts.RecipientName);
        }

        if (command.CustomerId is not { } customerId)
        {
            return NotificationErrors.RecipientUnresolved(Guid.Empty);
        }

        var customer = await sender.Send(
            new GetCustomerSummaryQuery(customerId), cancellationToken);

        if (customer.IsError)
        {
            return NotificationErrors.RecipientUnresolved(customerId);
        }

        return NotificationAddress.Create(
            customer.Value.Email,
            command.Facts.RecipientName ?? customer.Value.Name);
    }

    /// <summary>
    /// Records a notification that was deliberately not sent.
    ///
    /// <para>
    /// A row with a reason, so the decision is auditable. It needs a recipient to exist at all,
    /// and by definition there is not one — so a sentinel address is used and the reason says
    /// what happened. Ugly, and better than the alternative: the alternative is no row, and no
    /// row is how Onyx managed to send nothing for its entire life without anybody noticing.
    /// </para>
    /// </summary>
    private async Task<ErrorOr<EnqueuedNotification>> SuppressAsync(
        EnqueueNotificationCommand command,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "A {Kind} notification for {Reference} has no resolvable recipient ({Reason}). "
            + "Recorded as suppressed rather than dropped.",
            command.Kind,
            command.Reference,
            reason);

        var placeholder = NotificationAddress.Create("unresolved@invalid.local", null);

        var notification = NotificationRequest.Queue(
            command.Kind,
            command.Channel,
            command.Reference,
            command.CustomerId,
            placeholder.Value,
            new RenderedNotification(
                $"[suppressed] {command.Kind}",
                $"Not sent: {reason}",
                ContainsSecret: false),
            nowUtc);

        if (notification.IsError)
        {
            return notification.Errors;
        }

        var suppressed = notification.Value.Suppress(reason, nowUtc);

        if (suppressed.IsError)
        {
            return suppressed.Errors;
        }

        notifications.Add(notification.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new EnqueuedNotification(notification.Value.Id, false, Suppressed: true);
    }
}
