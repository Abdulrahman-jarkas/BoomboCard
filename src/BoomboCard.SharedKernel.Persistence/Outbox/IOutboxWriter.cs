using BoomboCard.SharedKernel;

namespace BoomboCard.SharedKernel.Persistence.Outbox;

/// <summary>
/// How a domain event handler reaches another bounded context.
///
/// Handlers may not call across a boundary directly, because that call is I/O inside the
/// command's transaction — Onyx A12 held a transaction open across three payment-gateway HTTP
/// calls, which on a single-writer database blocked every write in the system for the
/// duration. Instead a handler writes here, the row commits with the aggregate, and a
/// background dispatcher publishes it afterwards.
/// </summary>
public interface IOutboxWriter
{
    void Enqueue(IIntegrationEvent integrationEvent);
}

/// <summary>
/// One module's outbox, identified by that module's own <c>DbContext</c> type — for the same
/// reason as <see cref="IUnitOfWork{TScope}"/>. An outbox row only commits atomically with the
/// state change that produced it if both are tracked by the <b>same</b> context, so being
/// handed another module's writer breaks the single property the pattern exists to provide.
/// </summary>
public interface IOutboxWriter<TScope> : IOutboxWriter;
