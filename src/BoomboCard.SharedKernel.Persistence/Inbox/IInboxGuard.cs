using BoomboCard.SharedKernel;

namespace BoomboCard.SharedKernel.Persistence.Inbox;

/// <summary>
/// The first line of every integration event handler.
///
/// <code>
/// public async Task Handle(OrderConfirmedIntegrationEvent notification, CancellationToken ct)
/// {
///     if (!await inbox.TryClaimAsync(notification, GetType(), ct))
///     {
///         return;                          // already handled; this is a redelivery
///     }
///
///     // ... the work, then ONE SaveChangesAsync, which writes the claim with it
/// }
/// </code>
///
/// See <see cref="InboxMessage"/> for why redeliveries are a certainty rather than a
/// possibility.
/// </summary>
public interface IInboxGuard
{
    /// <summary>
    /// Claims an event for one consumer, returning <c>false</c> if that consumer has already
    /// handled it.
    ///
    /// <para>
    /// The claim row is <b>added to the change tracker, not saved</b>. It commits with the
    /// handler's own work in the handler's own transaction, which is the only arrangement where
    /// "this was consumed" and "here is what consuming it did" cannot disagree. Saving the claim
    /// separately would allow a crash between the two, leaving an event marked consumed with
    /// none of its effects applied — permanently, because the claim suppresses every retry.
    /// </para>
    ///
    /// <para>
    /// The <c>false</c> path is a read, so two deliveries racing in parallel can both pass it.
    /// That is deliberate and safe: the composite primary key rejects the second insert, the
    /// transaction rolls back, and the message is retried — at which point the read sees the
    /// committed row and returns <c>false</c>. The constraint is the guarantee; the read is the
    /// fast path that keeps the common case off the exception route.
    /// </para>
    /// </summary>
    Task<bool> TryClaimAsync(
        IIntegrationEvent integrationEvent,
        Type consumer,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One module's inbox, identified by that module's own <c>DbContext</c> type — for the same
/// reason as <see cref="IUnitOfWork{TScope}"/>. The claim has to be tracked by the same context
/// as the work it guards, or it does not commit with it.
/// </summary>
public interface IInboxGuard<TScope> : IInboxGuard;
