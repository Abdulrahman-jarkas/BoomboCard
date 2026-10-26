using BoomboCard.Customers.Contracts;
using BoomboCard.SharedKernel;

namespace BoomboCard.Customers.Domain.CustomerAggregate.Events;

/*
 * Customers' internal facts. Ids and primitives only, immutable records, no aggregate
 * instances — asserted by Events_CarryIdsAndPrimitives_NotAggregates.
 *
 * Raised for every state change, including the ones nothing listens to yet. The event is the
 * record of the fact; adding an audit projection or a notification later must not require
 * editing the aggregate.
 *
 * None of these carry a password, a verifier, or a token. The tokens that MUST travel (email
 * verification, password reset) are issued by their own aggregates and reach Notifications
 * through those, not through here.
 */

public sealed record CustomerRegisteredEvent(
    Guid CustomerId,
    string Email,
    string Name,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CustomerProfileUpdatedEvent(
    Guid CustomerId,
    string PreviousName,
    string NewName,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// <paramref name="ViaReset"/> distinguishes "the customer changed it knowing the old one" from
/// "someone used a single-use token from their inbox". Both are legitimate; only one of them is
/// what an account takeover looks like, so the notification has to be able to tell them apart.
/// </summary>
public sealed record CustomerPasswordChangedEvent(
    Guid CustomerId,
    string Email,
    bool ViaReset,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CustomerEmailVerifiedEvent(
    Guid CustomerId,
    string Email,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Carries the actor. A privilege change with no attributable author is the audit record Onyx
/// did not have (A40) — and S3 meant the author was usually the beneficiary.
/// </summary>
public sealed record CustomerRoleChangedEvent(
    Guid CustomerId,
    CustomerRole PreviousRole,
    CustomerRole NewRole,
    Guid ChangedByCustomerId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CustomerSuspendedEvent(
    Guid CustomerId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CustomerReinstatedEvent(
    Guid CustomerId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Deliberately carries no personal data — publishing the erased name or address in the event
/// that announces their erasure would leave both sitting in the outbox payload, which is a row
/// in the same database.
/// </summary>
public sealed record CustomerAnonymisedEvent(
    Guid CustomerId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CustomerAddressAddedEvent(
    Guid CustomerId,
    Guid AddressId,
    string CountryCode,
    bool IsDefault,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CustomerAddressUpdatedEvent(
    Guid CustomerId,
    Guid AddressId,
    string CountryCode,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CustomerAddressRemovedEvent(
    Guid CustomerId,
    Guid AddressId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record CustomerDefaultAddressChangedEvent(
    Guid CustomerId,
    Guid? PreviousAddressId,
    Guid NewAddressId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The sign-in attempt that crossed the lockout threshold.
///
/// Raised by the aggregate so the fact is recorded whether or not anything listens today —
/// Phase 7's alerting is the obvious consumer, and Onyx had no audit of auth attempts at all
/// (S12).
/// </summary>
public sealed record CustomerLockedOutEvent(
    Guid CustomerId,
    int FailedAttemptCount,
    DateTimeOffset LockedOutUntilUtc,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
