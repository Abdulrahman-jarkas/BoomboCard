namespace BoomboCard.SharedKernel;

public interface IDomainEventDispatcher
{
    /// <summary>
    /// Publishes every pending domain event, persists whatever the handlers changed, and
    /// repeats until no events remain — so second-order events raised by a handler are not
    /// dropped. Talabat, dome-gym and RiverBooks all dispatch in a single pass, and only
    /// cascade by accident when a handler happens to call SaveChanges and re-enter the
    /// override.
    /// </summary>
    Task DispatchUntilDrainedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One module's dispatcher, identified by that module's own <c>DbContext</c> type — for the
/// same reason as <see cref="IUnitOfWork{TScope}"/>. The dispatcher finds pending events by
/// walking a change tracker, so being handed another module's context means draining the wrong
/// one: every event raised by the command silently fails to publish.
/// </summary>
public interface IDomainEventDispatcher<TScope> : IDomainEventDispatcher;
