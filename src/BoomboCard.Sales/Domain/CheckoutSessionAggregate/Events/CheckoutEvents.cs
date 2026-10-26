using BoomboCard.SharedKernel;

namespace BoomboCard.Sales.Domain.CheckoutSessionAggregate.Events;

/*
 * Checkout's internal facts. Ids and primitives only, immutable records, no aggregate instances.
 */

public sealed record CheckoutStartedEvent(
    Guid CheckoutSessionId,
    Guid CustomerId,
    string CountryCode,
    decimal Total,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CheckoutRepricedEvent(
    Guid CheckoutSessionId,
    decimal PreviousTotal,
    decimal NewTotal,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CheckoutHoldingEvent(
    Guid CheckoutSessionId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CheckoutConfirmedEvent(
    Guid CheckoutSessionId,
    Guid OrderId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The session's window passed. <b>This is the event that drives recovery layer 2</b> — the one
/// the saga actually relies on, because inline compensation can fail for the same reason the
/// step it is compensating for failed.
/// </summary>
public sealed record CheckoutExpiredEvent(
    Guid CheckoutSessionId,
    Guid CustomerId,
    bool WasHolding,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CheckoutCancelledEvent(
    Guid CheckoutSessionId,
    Guid CustomerId,
    bool WasHolding,
    string Reason,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
