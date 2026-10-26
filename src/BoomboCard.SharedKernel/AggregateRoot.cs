namespace BoomboCard.SharedKernel;

public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>
    /// Optimistic concurrency token. Incremented by the persistence layer on every save and
    /// included in the UPDATE predicate, so a concurrent writer loses and is retried against
    /// fresh state instead of silently overwriting.
    /// Onyx D1: stock was read, compared and written with no token, so two orders for the last
    /// items both passed the check and both wrote the same decremented value.
    /// </summary>
    public uint Version { get; private set; }

    protected AggregateRoot(Guid id) : base(id) { }

    protected AggregateRoot() { }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// Records a domain event.
    ///
    /// Raise one for every state change, including changes nothing currently subscribes to.
    /// The event is the record of the fact; a listener added later (a projection, an outbox
    /// translator, an audit trail) must not require the aggregate to be edited before it
    /// starts firing.
    /// </summary>
    protected void RaiseDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    internal IReadOnlyList<IDomainEvent> PopDomainEvents()
    {
        var popped = _domainEvents.ToArray();
        _domainEvents.Clear();
        return popped;
    }

    internal void SetVersion(uint version) => Version = version;
}
