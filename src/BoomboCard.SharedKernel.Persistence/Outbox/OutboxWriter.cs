using System.Diagnostics;
using System.Text.Json;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Observability;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.SharedKernel.Persistence.Outbox;

public sealed class OutboxWriter<TContext>(TContext context) : IOutboxWriter<TContext>
    where TContext : DbContext
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly string ModuleName = BoomboCardTelemetry.ModuleNameOf(typeof(TContext));

    public void Enqueue(IIntegrationEvent integrationEvent)
    {
        var type = integrationEvent.GetType();

        var message = OutboxMessage.Create(
            integrationEvent.EventId,
            type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
            JsonSerializer.Serialize(integrationEvent, type, SerializerOptions),
            integrationEvent.OccurredAtUtc,

            // Captured here, from the command's own span, and stored on the row. This is the end
            // of the trace that the dispatcher will pick back up — see TraceContext for why the
            // context has to travel with the message rather than being inferred later.
            TraceContext.CaptureTraceParent(),
            TraceContext.CaptureTraceState());

        // Added to the same change tracker, so it is written by the same SaveChanges inside
        // the same transaction as the aggregate change that produced it.
        context.Set<OutboxMessage>().Add(message);

        // An event on the current span, not a span of its own. Enqueuing is an in-memory Add
        // with no duration worth measuring, and a span per enqueue would put a flat row under
        // every command for no information. As an event it shows up on the command's timeline,
        // which answers the question that is actually asked of it: did this command queue what
        // it was supposed to queue?
        //
        // Deliberately recorded even when the row's trace columns came back null, because then
        // it is the only place the enqueue appears at all.
        Activity.Current?.AddEvent(new ActivityEvent(
            BoomboCardTelemetry.OutboxEnqueuedEvent,
            tags: new ActivityTagsCollection
            {
                { BoomboCardTelemetry.ModuleTag, ModuleName },
                { BoomboCardTelemetry.MessageTypeTag, type.Name },
                { BoomboCardTelemetry.EventKindTag, "integration" },
                { BoomboCardTelemetry.MessagingSystemTag, BoomboCardTelemetry.MessagingSystem },
                { BoomboCardTelemetry.MessagingOperationTag, "send" },
                { BoomboCardTelemetry.MessagingMessageIdTag, integrationEvent.EventId },
                { BoomboCardTelemetry.MessagingDestinationTag, ModuleName }
            }));
    }
}
