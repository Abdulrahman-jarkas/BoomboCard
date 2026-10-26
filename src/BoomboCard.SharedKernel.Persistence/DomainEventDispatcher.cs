using System.Diagnostics;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Observability;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.SharedKernel.Persistence;

/// <summary>
/// Drains and publishes domain events for one module's DbContext.
///
/// Ordering, and why it is this way:
///  1. The caller has already saved. Handlers therefore observe persisted state — Talabat
///     publishes before base.SaveChangesAsync(), so a handler reading the database sees the
///     state as it was before the command ran.
///  2. We run inside the command's transaction, opened by UnitOfWorkBehavior. A handler that
///     throws rolls the whole command back — RiverBooks dispatches after an autocommitted
///     save, so the aggregate is already durable when a handler fails.
///  3. We loop. A handler may mutate another aggregate and raise its own events; a single
///     pass silently drops those.
///  4. Events are popped before publishing, so a handler that triggers another save does not
///     re-publish what is already in flight.
///
/// Exceptions propagate. dome-gym's middleware wraps dispatch in
/// <c>catch (EventualConsistencyException) { }</c>, which rolls the transaction back and tells
/// nobody — after the client has already received 200.
///
/// <para>
/// Each event gets its own span, nested under the command that caused it and under each other
/// as the cascade deepens. The cascade is the thing worth being able to see here: (3) means one
/// command can set off several rounds of events, each round able to raise the next, and the
/// shape of that is invisible in logs — which is also why the <see cref="MaxCascadeDepth"/>
/// failure below is so hard to diagnose without a trace. The depth is recorded as an attribute
/// on every event span, so a cascade creeping toward the cap is visible before it hits it.
/// </para>
/// </summary>
public sealed class DomainEventDispatcher<TContext>(TContext context, IPublisher publisher)
    : IDomainEventDispatcher<TContext>
    where TContext : DbContext
{
    /// <summary>
    /// Guards against two handlers that raise each other's events. Without a cap that loops
    /// forever while holding a database transaction open.
    /// </summary>
    private const int MaxCascadeDepth = 10;

    private static readonly string ModuleName = BoomboCardTelemetry.ModuleNameOf(typeof(TContext));

    public async Task DispatchUntilDrainedAsync(CancellationToken cancellationToken = default)
    {
        for (var depth = 0; depth < MaxCascadeDepth; depth++)
        {
            var pending = context.ChangeTracker
                .Entries<AggregateRoot>()
                .Where(entry => entry.Entity.DomainEvents.Count > 0)
                .SelectMany(entry => DomainEventAccess.Drain(entry.Entity))
                .ToList();

            if (pending.Count == 0)
            {
                return;
            }

            foreach (var domainEvent in pending)
            {
                await PublishAsync(domainEvent, depth, cancellationToken);
            }

            // Handlers mutate aggregates; they never save. Persisting here keeps that rule
            // enforceable and means the next iteration sees a clean change tracker.
            if (context.ChangeTracker.HasChanges())
            {
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Domain event cascade exceeded {MaxCascadeDepth} levels in {typeof(TContext).Name}. " +
            "Two handlers are most likely raising each other's events.");
    }

    private async Task PublishAsync(IDomainEvent domainEvent, int depth, CancellationToken cancellationToken)
    {
        var eventName = domainEvent.GetType().Name;

        // No explicit parent: a domain event is published on the same async call stack as the
        // command that raised it, so the ambient Activity is already the right parent. This is
        // the case the outbox is NOT, and the contrast is the point — in-process events need no
        // propagation machinery, which is exactly why the outbox's need for it is easy to miss.
        using var activity = BoomboCardTelemetry.Source.StartActivity(
            $"domain event {eventName}",
            ActivityKind.Internal);

        activity?.SetTag(BoomboCardTelemetry.ModuleTag, ModuleName);
        activity?.SetTag(BoomboCardTelemetry.MessageTypeTag, eventName);
        activity?.SetTag(BoomboCardTelemetry.EventKindTag, "domain");
        activity?.SetTag(BoomboCardTelemetry.CascadeDepthTag, depth);

        try
        {
            await publisher.Publish(domainEvent, cancellationToken);
        }
        catch (Exception exception)
        {
            // Recorded, then rethrown unchanged. The rethrow is the documented behaviour above
            // and is what rolls the command back; recording it first means the span that failed
            // is the event's own rather than only the command's, so a trace says WHICH handler
            // in a cascade of twelve took the transaction down.
            activity.RecordFailure(exception);
            throw;
        }
    }
}
