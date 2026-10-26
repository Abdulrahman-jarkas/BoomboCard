using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Domain.CustomerAggregate;
using ErrorOr;
using MediatR;

namespace BoomboCard.Customers.Application.Customers.Queries;

/*
 * Queries. None of these implement ICustomersRequest, so no transaction is opened for them —
 * dome-gym's middleware called BeginTransactionAsync() at the top of every request including
 * pure reads and held it for the whole request lifetime.
 *
 * Every one returns an explicit response record. No aggregate is reachable from any of them,
 * which is what makes Onyx S11 structurally impossible: it serialised its Customer entity from
 * three endpoints, so PasswordHash went over the wire on every profile read.
 */

/// <summary>One address, as the owner sees it.</summary>
public sealed record AddressResponse(
    Guid AddressId,
    string Label,
    string Line1,
    string? Line2,
    string City,
    string? PostalCode,
    string CountryCode,
    bool IsDefault);

/// <summary>
/// The signed-in customer's own view of their account.
///
/// Note the absence of a password field of any kind — not the verifier, not a redacted form of
/// it, not a "has password" flag. The closest this gets is
/// <paramref name="PasswordUpdatedAtUtc"/>, which is a date the account owner may legitimately
/// want to see.
/// </summary>
public sealed record CustomerProfileResponse(
    Guid CustomerId,
    string Email,
    string Name,
    CustomerRole Role,
    string Status,
    bool EmailVerified,
    bool CanTransact,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? LastSignedInAtUtc,
    DateTimeOffset PasswordUpdatedAtUtc,
    IReadOnlyList<AddressResponse> Addresses);

public sealed record GetCustomerProfileQuery(Guid CustomerId)
    : IRequest<ErrorOr<CustomerProfileResponse>>;

/// <summary>
/// One row of the administrative customer list.
///
/// <para>
/// <paramref name="AddressCount"/> is computed <b>in SQL</b>, which is the point of this record
/// existing. Onyx A26 built the same list by materialising every customer and then running
/// <c>_context.Orders.Count(o =&gt; o.CustomerId == c.Id)</c> inside a <c>Select</c> — one extra
/// round trip per customer, forever, invisible in code review because it reads like LINQ.
/// </para>
/// </summary>
public sealed record CustomerListItem(
    Guid CustomerId,
    string Email,
    string Name,
    CustomerRole Role,
    string Status,
    bool EmailVerified,
    int AddressCount,
    DateTimeOffset RegisteredAtUtc);

/// <summary>
/// A page of customers, plus the cursor for the next one.
///
/// Keyset paged over email, which is unique and ordered — so no row can be seen twice or
/// skipped however much the table changes between requests. Onyx A27 used
/// <c>.Skip(skip).Take(take)</c> with no <c>ORDER BY</c> at all, so the "pages" were whatever
/// the engine felt like returning.
/// </summary>
public sealed record CustomerPage(IReadOnlyList<CustomerListItem> Items, string? NextCursor);

/// <summary>
/// Filters, plus the keyset cursor.
///
/// <c>Cursor</c> is the previous page's last email, or null for the first page. <c>Limit</c> is
/// capped by the handler — Onyx A28 had no bound at all, so <c>?take=1000000</c> was a
/// one-request denial of service against a table with no indexes (D11).
/// </summary>
public sealed record SearchCustomersQuery(
    string? Search = null,
    CustomerStatusValue? Status = null,
    CustomerRole? Role = null,
    bool? EmailVerified = null,
    string? Cursor = null,
    int Limit = 50) : IRequest<ErrorOr<CustomerPage>>;

/// <summary>
/// Recent sign-in attempts for one account, newest first.
///
/// <para>
/// Readable by the account's owner as well as by an administrator, which is deliberate: the
/// person best placed to notice "someone tried to sign in to my account forty times from
/// somewhere I have never been" is the account's owner. Onyx recorded none of this (S12), so
/// neither party could have noticed anything.
/// </para>
/// </summary>
public sealed record AuthAttemptResponse(
    string Outcome,
    string? ClientFingerprint,
    DateTimeOffset OccurredAtUtc);

public sealed record GetAuthAttemptsQuery(Guid CustomerId, int Limit = 50)
    : IRequest<ErrorOr<List<AuthAttemptResponse>>>;
