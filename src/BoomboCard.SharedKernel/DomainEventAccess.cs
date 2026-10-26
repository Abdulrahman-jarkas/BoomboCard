namespace BoomboCard.SharedKernel;

/// <summary>
/// The only route by which the persistence layer may drain events from, or bump the version
/// of, an aggregate. Kept off AggregateRoot's own surface so domain and application code
/// cannot clear events or forge a version.
///
/// RiverBooks achieves the same by implementing IHaveDomainEvents.ClearDomainEvents()
/// explicitly; Talabat exposes PopDomainEvents() publicly, so any caller can silently
/// discard pending events.
/// </summary>
public static class DomainEventAccess
{
    public static IReadOnlyList<IDomainEvent> Drain(AggregateRoot aggregate) =>
        aggregate.PopDomainEvents();

    public static void SetVersion(AggregateRoot aggregate, uint version) =>
        aggregate.SetVersion(version);
}
