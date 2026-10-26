using MediatR;

namespace BoomboCard.SharedKernel;

/// <summary>
/// A fact that happened inside one bounded context.
///
/// Rules this codebase holds to:
///  - Carries ids and primitives only, never aggregate instances. dome-gym's
///    GymAddedEvent(Subscription, Gym) and RiverBooks' OrderCreatedEvent(Order) are both
///    unserialisable, and because their handlers run later they can observe state that moved
///    on after the fact was recorded.
///  - Dispatched AFTER the write and INSIDE the command's transaction, so a handler sees
///    persisted state and its own work commits or rolls back together with the command.
///    Talabat publishes before SaveChangesAsync; RiverBooks dispatches after the save but
///    outside any transaction.
///  - Handlers may mutate aggregates. They may NOT perform I/O or call another bounded
///    context — write an integration event to the outbox for that.
/// </summary>
public interface IDomainEvent : INotification
{
    DateTimeOffset OccurredAtUtc { get; }
}
