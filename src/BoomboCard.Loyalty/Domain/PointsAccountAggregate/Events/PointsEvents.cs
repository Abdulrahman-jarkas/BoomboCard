using BoomboCard.Loyalty.Domain.Ledger;
using BoomboCard.SharedKernel;

namespace BoomboCard.Loyalty.Domain.PointsAccountAggregate.Events;

/*
 * Loyalty's internal facts. Ids and primitives only, immutable records, no aggregate instances
 * — asserted by Events_CarryIdsAndPrimitives_NotAggregates.
 *
 * Raised for every movement, including the ones nothing listens to yet.
 */

public sealed record PointsAccountOpenedEvent(
    Guid AccountId,
    Guid CustomerId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Carries the reason and the balance after, so a consumer never has to re-derive either. One
/// event for every movement rather than one per reason: the shape is identical, and a listener
/// that cares about only some of them filters on <paramref name="Reason"/>.
/// </summary>
public sealed record PointsMovedEvent(
    Guid AccountId,
    Guid CustomerId,
    int Delta,
    PointsReason Reason,
    string Reference,
    int BalanceAfter,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The account was closed because its customer was anonymised, forfeiting whatever remained.
/// Separate from <see cref="PointsMovedEvent"/> because closing is a lifecycle change rather
/// than a movement, even though it produces one.
/// </summary>
public sealed record PointsAccountClosedEvent(
    Guid AccountId,
    Guid CustomerId,
    int ForfeitedPoints,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
