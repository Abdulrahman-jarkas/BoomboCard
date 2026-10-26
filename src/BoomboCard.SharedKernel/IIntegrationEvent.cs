using MediatR;

namespace BoomboCard.SharedKernel;

/// <summary>
/// A fact published across a context boundary. Lives in a *.Contracts project, carries
/// flattened primitives, and is delivered through the transactional outbox — so the message
/// commits atomically with the state change that produced it and is retried until delivered.
///
/// Onyx A8/A9: the order confirmation was <c>_ = Task.Run(() =&gt; SendConfirmationEmail(...))</c>
/// on a method that threw, and the payment notification fired before the transaction
/// committed. Nothing was ever delivered and nothing recorded that.
///
/// Delivery is at-least-once, so every handler MUST be idempotent.
/// </summary>
public interface IIntegrationEvent : INotification
{
    Guid EventId { get; }

    DateTimeOffset OccurredAtUtc { get; }
}
