using BoomboCard.Notifications.Contracts;
using BoomboCard.Notifications.Data;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence;

namespace BoomboCard.Notifications.Application;

/// <summary>
/// Notifications' transaction boundary: <see cref="ModuleUnitOfWorkBehavior{TRequest,TResponse}"/>
/// closed over this module's context and constrained to its requests.
///
/// <para>
/// <b>What it does not wrap is the point here.</b> <c>DispatchNotificationCommand</c> deliberately
/// carries no <see cref="INotificationsRequest"/> marker, so it is never wrapped — it talks to a
/// mail provider, and Onyx <b>A12</b> held a transaction across exactly that kind of call. It
/// dispatches two marked commands instead: one to claim the notification before the send, one to
/// record the outcome afterwards.
/// </para>
/// </summary>
internal sealed class NotificationsUnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork<NotificationsDbContext> unitOfWork,
    IDomainEventDispatcher<NotificationsDbContext> dispatcher)
    : ModuleUnitOfWorkBehavior<TRequest, TResponse>(unitOfWork, dispatcher)
    where TRequest : notnull, INotificationsRequest;
