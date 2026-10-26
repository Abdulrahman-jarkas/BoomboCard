using BoomboCard.Promotions.Contracts;
using BoomboCard.SharedKernel;

namespace BoomboCard.Promotions.Domain.PromotionAggregate.Events;

/*
 * Promotions' internal facts. Ids and primitives only, immutable records, no aggregate
 * instances — asserted by Events_CarryIdsAndPrimitives_NotAggregates.
 *
 * Raised for every state change, including ones nothing listens to yet. PromotionCreatedEvent
 * and PromotionReconfiguredEvent have no handlers today: the event is the record of the fact,
 * and adding an audit projection later must not require editing the aggregate.
 */

public sealed record PromotionCreatedEvent(
    Guid PromotionId,
    string Code,
    string Description,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The terms changed while the promotion was still a draft. Only a draft can be reconfigured —
/// a live campaign's terms are something customers may already have been shown.
/// </summary>
public sealed record PromotionReconfiguredEvent(
    Guid PromotionId,
    string Code,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record PromotionActivatedEvent(
    Guid PromotionId,
    string Code,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record PromotionPausedEvent(
    Guid PromotionId,
    string Code,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// <paramref name="Reason"/> distinguishes the three ways a campaign ends, because they mean
/// completely different things to whoever ran it: exhausted is a success, expired is a schedule
/// working, and withdrawn is a decision. Onyx could not tell them apart because it had no
/// campaign state and no reporting — its one expired code (**M16**) looked identical to a code
/// nobody had used.
/// </summary>
public sealed record PromotionClosedEvent(
    Guid PromotionId,
    string Code,
    PromotionCloseReason Reason,
    int RedemptionCount,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public enum PromotionCloseReason
{
    /// <summary>The usage cap was reached.</summary>
    Exhausted = 0,

    /// <summary>The validity window ended.</summary>
    Expired = 1,

    /// <summary>Closed by hand before it had to be.</summary>
    Withdrawn = 2
}

public sealed record PromotionRedeemedEvent(
    Guid PromotionId,
    string Code,
    Guid CustomerId,
    string CheckoutSessionId,
    int RedemptionCount,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record PromotionReleasedEvent(
    Guid PromotionId,
    string Code,
    Guid CustomerId,
    string CheckoutSessionId,
    ReleaseReason Reason,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
