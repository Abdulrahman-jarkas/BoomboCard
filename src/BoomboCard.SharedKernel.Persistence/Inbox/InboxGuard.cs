using System.Diagnostics;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Observability;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.SharedKernel.Persistence.Inbox;

public sealed class InboxGuard<TContext>(TContext context, TimeProvider timeProvider)
    : IInboxGuard<TContext>
    where TContext : DbContext
{
    public async Task<bool> TryClaimAsync(
        IIntegrationEvent integrationEvent,
        Type consumer,
        CancellationToken cancellationToken = default)
    {
        var consumerName = consumer.FullName ?? consumer.Name;

        // Checks the change tracker as well as the database, so a handler that claims twice
        // within one transaction is caught here rather than at SaveChanges.
        var local = context.Set<InboxMessage>().Local.Any(message =>
            message.EventId == integrationEvent.EventId && message.Consumer == consumerName);

        if (local)
        {
            RecordOutcome(consumerName, duplicate: true);
            return false;
        }

        var alreadyConsumed = await context.Set<InboxMessage>()
            .AnyAsync(
                message => message.EventId == integrationEvent.EventId
                    && message.Consumer == consumerName,
                cancellationToken);

        if (alreadyConsumed)
        {
            RecordOutcome(consumerName, duplicate: true);
            return false;
        }

        var eventType = integrationEvent.GetType();

        context.Set<InboxMessage>().Add(InboxMessage.Claim(
            integrationEvent.EventId,
            consumerName,
            eventType.FullName ?? eventType.Name,
            timeProvider.GetUtcNow()));

        RecordOutcome(consumerName, duplicate: false);

        return true;
    }

    /// <summary>
    /// Notes on the current span — the outbox publish span, since that is what every consumer
    /// runs inside — which consumer this was and whether it did the work or recognised a
    /// redelivery and returned.
    ///
    /// <para>
    /// This is the single most useful attribute in the whole flow, because a skipped duplicate
    /// is <b>indistinguishable from a successful handler</b> everywhere else. Both return
    /// normally, both commit, both mark the message published, and the consumer deliberately
    /// logs nothing — at-least-once delivery makes a duplicate routine, so logging one would be
    /// noise. The result is that "the points never arrived" and "the points arrived once and the
    /// second delivery correctly did nothing" look identical in the logs, and are the two
    /// answers you need to tell apart.
    /// </para>
    ///
    /// <para>
    /// An event rather than a span: a claim is one indexed read and an Add, and the question is
    /// what happened, not how long it took. Recorded per consumer, because a single event
    /// usually fans out to several and they are claimed independently — so one span can
    /// legitimately carry a claim for one consumer and a duplicate for another, and both belong
    /// on it.
    /// </para>
    /// </summary>
    private static void RecordOutcome(string consumerName, bool duplicate)
    {
        var activity = Activity.Current;

        if (activity is null)
        {
            return;
        }

        activity.SetTag(BoomboCardTelemetry.InboxConsumerTag, consumerName);
        activity.SetTag(BoomboCardTelemetry.InboxDuplicateTag, duplicate);

        activity.AddEvent(new ActivityEvent(
            duplicate
                ? BoomboCardTelemetry.InboxDuplicateEvent
                : BoomboCardTelemetry.InboxClaimedEvent,
            tags: new ActivityTagsCollection
            {
                { BoomboCardTelemetry.InboxConsumerTag, consumerName }
            }));
    }
}
