namespace BoomboCard.SharedKernel.Persistence.Inbox;

/// <summary>
/// A record that one consumer has already handled one integration event.
///
/// <para>
/// This exists because of how <c>OutboxDispatcher</c> has to behave. It publishes a message
/// through MediatR, which fans out to <b>every</b> handler subscribed to that contract, and
/// then marks the message as a whole. If the second of three consumers throws, the message is
/// marked failed and retried — and on the retry the first consumer, which already succeeded,
/// runs again. Marking per consumer instead would mean tracking partial progress in the
/// publishing module, which has no business knowing who is listening.
/// </para>
///
/// <para>
/// So delivery is at-least-once, exactly as <c>IIntegrationEvent</c> says, and the rule
/// "every handler MUST be idempotent" needed a mechanism rather than a comment. This is it:
/// a consumer claims <c>(EventId, Consumer)</c> before doing its work, inside the same
/// transaction as that work, and a second delivery finds the row and returns.
/// </para>
///
/// <para>
/// The claim is a row with a unique primary key, not a read-then-write check, so two parallel
/// deliveries of the same message cannot both pass — the loser gets a duplicate-key violation
/// rather than running the handler twice. Same reasoning as Catalog's
/// <c>(ProductId, Reference, Reason)</c> index: an application-level check has a race between
/// the look and the leap, and a constraint does not.
/// </para>
///
/// <para>
/// Catalog's stock movements are additionally idempotent in the domain, on the checkout session
/// id. That is not redundant — it protects against a retried <i>checkout</i>, which is a
/// different event from a redelivered <i>message</i>, and it is the layer that lets the expiry
/// sweeper fire <c>Restore</c> unconditionally.
/// </para>
/// </summary>
public sealed class InboxMessage
{
    public Guid EventId { get; private set; }

    /// <summary>
    /// Which handler consumed it. Part of the key, because two consumers of the same event are
    /// independent and each must get its own turn.
    /// </summary>
    public string Consumer { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public DateTimeOffset ConsumedAtUtc { get; private set; }

    private InboxMessage() { }

    public static InboxMessage Claim(
        Guid eventId,
        string consumer,
        string eventType,
        DateTimeOffset nowUtc) =>
        new()
        {
            EventId = eventId,
            Consumer = consumer,
            EventType = eventType,
            ConsumedAtUtc = nowUtc
        };
}
