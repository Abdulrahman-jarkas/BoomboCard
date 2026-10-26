using ErrorOr;
using MediatR;

namespace BoomboCard.Customers.Contracts;

/// <summary>
/// The minimum another module needs to know about a customer: that they exist, that they can
/// transact, and where to reach them.
///
/// <para>
/// Deliberately not the aggregate, and deliberately small. Onyx S11 serialised its
/// <c>Customer</c> entity straight to the wire from three endpoints, so every response carried
/// <c>PasswordHash</c> — and its unused DTO classes sat in a <c>Dtos.cs</c> nobody referenced
/// (A24).
/// </para>
///
/// <para>
/// <c>CanTransact</c> means active and email-verified. One flag rather than two booleans a
/// caller has to combine correctly: Sales needs the answer to "may this customer check out",
/// and the rule for that belongs here rather than duplicated in every consumer.
/// </para>
/// </summary>
public sealed record CustomerSummaryResponse(
    Guid CustomerId,
    string Email,
    string Name,
    bool CanTransact);

/// <summary>
/// A query, so no transaction. Used by Sales to confirm a checkout's customer is real and may
/// transact before anything is priced, and by Notifications to address a message.
/// </summary>
public sealed record GetCustomerSummaryQuery(Guid CustomerId)
    : IRequest<ErrorOr<CustomerSummaryResponse>>;
