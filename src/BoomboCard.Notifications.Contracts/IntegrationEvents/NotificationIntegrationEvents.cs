using BoomboCard.SharedKernel;

namespace BoomboCard.Notifications.Contracts.IntegrationEvents;

/*
 * Notifications publishes exactly one event, and the restraint is deliberate.
 *
 * "A message was queued" and "a message was sent" are facts no other module can act on, and every
 * published contract is a coupling maintained forever. Publishing them would be the opposite of
 * the A32 lesson: not a capability with no consumer, but a consumer surface with no capability
 * behind it.
 *
 * What DOES need to leave this module is a failure, because that is precisely what Onyx could not
 * produce.
 */

/// <summary>
/// A notification will not be delivered.
///
/// <para>
/// <b>This single event is the difference between this module and what it replaces.</b> Onyx
/// <b>A8</b>: <i>"No customer has ever received any communication from this system, and nothing
/// records that. There is no retry, no dead-letter, no delivery record."</i> There is now a retry,
/// a dead-letter and a delivery record — and this is the dead letter leaving the module so
/// somebody can be told.
/// </para>
///
/// <para>
/// <paramref name="Definitive"/> separates two failures that need different responses:
/// </para>
///
/// <list type="bullet">
/// <item><b>Definitive</b> — the provider rejected the address. The customer's email is wrong and
/// somebody has to ask them for a new one. Retrying is pointless.</item>
/// <item><b>Not definitive</b> — five transport failures and the budget is spent. The provider was
/// down and the message is genuinely lost. For a password reset that means a customer is locked
/// out and does not know why, which is the case most worth paging someone about.</item>
/// </list>
///
/// <para>
/// <paramref name="Recipient"/> travels so an operator can see <i>who</i> was not reached without
/// querying another module. It is an email address rather than a credential, and the body — which
/// may hold a token — deliberately does not travel.
/// </para>
/// </summary>
public sealed record NotificationUndeliverableIntegrationEvent(
    Guid EventId,
    Guid NotificationId,
    string Kind,
    string Reference,
    Guid? CustomerId,
    string Recipient,
    string Reason,
    bool Definitive,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
