using BoomboCard.SharedKernel;

namespace BoomboCard.Promotions.Contracts.IntegrationEvents;

/*
 * Promotions' cross-boundary facts. Public, flattened to primitives, serialisable — they travel
 * through the outbox, which stores them as JSON.
 *
 * Translated from internal domain events by handlers in BoomboCard.Promotions, so the internal
 * events stay free to change without breaking a published contract.
 */

/// <summary>
/// A campaign has gone live. Notifications can announce it; reporting can start a row for it.
/// </summary>
public sealed record PromotionActivatedIntegrationEvent(
    Guid EventId,
    Guid PromotionId,
    string Code,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// A campaign has stopped accepting uses — paused by hand, exhausted, or past its window.
///
/// <para>
/// Carries <paramref name="Reason"/> because the three cases need different responses: an
/// exhausted campaign that converted well is a success worth repeating, and one paused on day
/// two is not. Onyx shipped <c>SUMMER25</c> already expired and had no way to notice (<b>M16</b>).
/// </para>
/// </summary>
public sealed record PromotionClosedIntegrationEvent(
    Guid EventId,
    Guid PromotionId,
    string Code,
    string Reason,
    int RedemptionCount,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// A use was claimed. Loyalty and reporting both consume this; the checkout session id is the
/// correlation handle back to the order it belongs to.
/// </summary>
public sealed record PromotionRedeemedIntegrationEvent(
    Guid EventId,
    Guid PromotionId,
    string Code,
    Guid CustomerId,
    string CheckoutSessionId,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// A claimed use was given back, because the checkout it belonged to did not complete.
/// </summary>
public sealed record PromotionReleasedIntegrationEvent(
    Guid EventId,
    Guid PromotionId,
    string Code,
    Guid CustomerId,
    string CheckoutSessionId,
    ReleaseReason Reason,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
