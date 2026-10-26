using BoomboCard.SharedKernel;

namespace BoomboCard.Loyalty.Contracts.IntegrationEvents;

/*
 * Loyalty's cross-boundary facts. Public, flattened to primitives, serialisable — they travel
 * through the outbox.
 *
 * Note what is NOT published: the balance after every movement. A consumer that wants the
 * balance asks GetPointsBalanceQuery, because a balance in a message is stale the moment it is
 * written and two consumers acting on different stale copies is worse than one query.
 */

/// <summary>
/// Points were credited. Notifications tells the customer — "you earned 49 points" is the half
/// of a loyalty scheme that makes customers aware they are in one, and Onyx had no notification
/// delivery of any kind (<b>A8</b>).
/// </summary>
public sealed record PointsEarnedIntegrationEvent(
    Guid EventId,
    Guid CustomerId,
    int Points,
    string OrderReference,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// Points were spent on a checkout.
///
/// <para>
/// <b>Carries the points and not the money.</b> The first version of this record also carried
/// <c>DiscountAmount</c> and <c>CurrencyCode</c> — and writing the translator exposed why that
/// was wrong: the debit is a count of points, the currency belongs to the basket, and Loyalty's
/// internal event does not know it. The only way to populate those fields was to guess a
/// currency, which is how a figure becomes confidently wrong.
/// </para>
///
/// <para>
/// The quote already recorded the priced discount line it added, and Sales owns that figure. A
/// second copy derived here would be a second thing that can drift from the first, which is
/// precisely Onyx <b>M14</b>'s shape — one fact written in two places that then disagreed.
/// A consumer needing the cash value asks for it where it is owned.
/// </para>
/// </summary>
public sealed record PointsRedeemedIntegrationEvent(
    Guid EventId,
    Guid CustomerId,
    int Points,
    string CheckoutSessionId,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// Points lapsed.
///
/// <para>
/// Published so Notifications can warn <i>before</i> it happens as well as report it after —
/// expiring a customer's points silently is the behaviour that makes loyalty schemes resented.
/// The warning itself is Phase 6's job; this event is what it needs.
/// </para>
/// </summary>
public sealed record PointsExpiredIntegrationEvent(
    Guid EventId,
    Guid CustomerId,
    int Points,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
