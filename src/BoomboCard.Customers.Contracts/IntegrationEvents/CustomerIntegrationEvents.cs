using BoomboCard.SharedKernel;

namespace BoomboCard.Customers.Contracts.IntegrationEvents;

/*
 * Customers' cross-boundary facts. Public, flattened to primitives, serialisable — they travel
 * through the outbox, which stores them as JSON.
 *
 * Translated from internal domain events by handlers in BoomboCard.Customers
 * (Application/Customers/IntegrationEvents), so the internal events stay free to change without
 * breaking a published contract.
 *
 * Note what these carry and what they do not. A verification or reset TOKEN is in the event,
 * because Notifications cannot send the email without it and the database holds only a hash.
 * Everything else here is a fact, not a secret: no password hash, no address, no role except on
 * the event whose subject IS the role change.
 */

/// <summary>
/// A new customer exists. Loyalty opens a ledger on this; Notifications sends a welcome.
/// </summary>
public sealed record CustomerRegisteredIntegrationEvent(
    Guid EventId,
    Guid CustomerId,
    string Email,
    string Name,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// Carries the plaintext token because this message is the only thing that can deliver it: the
/// database stores a hash, so after this point nobody — this system included — can recover it.
///
/// Onyx generated its reset token from <c>new Random()</c> over 36 characters, eight long, and
/// stored it in plaintext with no expiry and no single-use marker (S14).
/// </summary>
public sealed record EmailVerificationRequestedIntegrationEvent(
    Guid EventId,
    Guid CustomerId,
    string Email,
    string Token,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// Published only when the address belongs to a real account. The <i>endpoint</i> returns the
/// same 202 either way, so it cannot be used to enumerate registered addresses — see
/// <c>RequestPasswordResetCommand</c>.
/// </summary>
public sealed record PasswordResetRequestedIntegrationEvent(
    Guid EventId,
    Guid CustomerId,
    string Email,
    string Token,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// A password changed, by the customer or through a reset. Notifications tells the account
/// owner, which is how a successful credential-stuffing attempt reaches the only person able to
/// act on it.
/// </summary>
public sealed record CustomerPasswordChangedIntegrationEvent(
    Guid EventId,
    Guid CustomerId,
    string Email,
    bool ViaReset,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// A role was granted or revoked. Published so a privilege change lands in the audit trail as a
/// fact with an actor attached, rather than only as a changed column — Onyx had no audit of
/// anything (A40), and this is the single event most worth being able to reconstruct afterwards.
/// </summary>
public sealed record CustomerRoleChangedIntegrationEvent(
    Guid EventId,
    Guid CustomerId,
    CustomerRole PreviousRole,
    CustomerRole NewRole,
    Guid ChangedByCustomerId,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// The customer's personal data has been erased. Consumers holding a copy of a name or an email
/// — a notification log, a reporting projection — must erase theirs too, which is the half of an
/// erasure request that a single <c>DELETE</c> cannot do.
///
/// Onyx D13 hard-deleted the row and left its orders and payments pointing at a customer id that
/// no longer existed: it destroyed the financial record and did not achieve erasure either, since
/// the name was still sitting in every other table that had copied it.
/// </summary>
public sealed record CustomerAnonymisedIntegrationEvent(
    Guid EventId,
    Guid CustomerId,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
