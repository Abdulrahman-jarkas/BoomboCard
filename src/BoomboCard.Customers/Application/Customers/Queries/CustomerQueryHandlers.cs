using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Data;
using BoomboCard.Customers.Domain.CustomerAggregate;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Customers.Application.Customers.Queries;

internal sealed class GetCustomerProfileQueryHandler(CustomersDbContext context)
    : IRequestHandler<GetCustomerProfileQuery, ErrorOr<CustomerProfileResponse>>
{
    public async Task<ErrorOr<CustomerProfileResponse>> Handle(
        GetCustomerProfileQuery query,
        CancellationToken cancellationToken)
    {
        // Projected straight into the response record in SQL, so the aggregate is never
        // materialised and the verifier column is never even read, let alone serialised. That
        // is the structural version of "do not leak the password hash" — Onyx S11 returned
        // Ok(customer) and leaked it from three separate endpoints.
        var profile = await context.Customers
            .AsNoTracking()
            .Where(customer => customer.Id == query.CustomerId)
            .Select(customer => new CustomerProfileResponse(
                customer.Id,
                customer.Email.Value,
                customer.Name.Value,
                customer.Role,
                customer.Status.Value.ToString(),
                customer.EmailVerifiedAtUtc != null,
                customer.Status.Value == CustomerStatusValue.Active
                    && customer.EmailVerifiedAtUtc != null,
                customer.RegisteredAtUtc,
                customer.LastSignedInAtUtc,
                customer.Credential.UpdatedAtUtc,
                customer.Addresses
                    .OrderBy(address => address.Label)
                    .Select(address => new AddressResponse(
                        address.Id,
                        address.Label,
                        address.Line1,
                        address.Line2,
                        address.City,
                        address.PostalCode,
                        address.CountryCode,
                        customer.DefaultAddressId == address.Id))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);

        return profile is null ? CustomerErrors.NotFound(query.CustomerId) : profile;
    }
}

internal sealed class GetCustomerSummaryQueryHandler(CustomersDbContext context)
    : IRequestHandler<GetCustomerSummaryQuery, ErrorOr<CustomerSummaryResponse>>
{
    public async Task<ErrorOr<CustomerSummaryResponse>> Handle(
        GetCustomerSummaryQuery query,
        CancellationToken cancellationToken)
    {
        // CanTransact is projected rather than read from the aggregate's property, because the
        // property cannot be translated to SQL. The duplication is real and it is the kind that
        // caused Onyx M18, so there is a test asserting the projection and Customer.CanTransact
        // agree on every combination of status and verification.
        var summary = await context.Customers
            .AsNoTracking()
            .Where(customer => customer.Id == query.CustomerId)
            .Select(customer => new CustomerSummaryResponse(
                customer.Id,
                customer.Email.Value,
                customer.Name.Value,
                customer.Status.Value == CustomerStatusValue.Active
                    && customer.EmailVerifiedAtUtc != null))
            .FirstOrDefaultAsync(cancellationToken);

        return summary is null ? CustomerErrors.NotFound(query.CustomerId) : summary;
    }
}

internal sealed class SearchCustomersQueryHandler(CustomersDbContext context)
    : IRequestHandler<SearchCustomersQuery, ErrorOr<CustomerPage>>
{
    /// <summary>
    /// Onyx A28: <c>Search(int skip = 0, int take = 20)</c> with no validation, so
    /// <c>?take=1000000</c> was a one-request denial of service against a table with no indexes
    /// (D11).
    /// </summary>
    private const int MaxLimit = 200;

    public async Task<ErrorOr<CustomerPage>> Handle(
        SearchCustomersQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            return Error.Validation(
                "SearchCustomers.InvalidLimit",
                $"Limit must be between 1 and {MaxLimit}.");
        }

        var customers = context.Customers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Lower-cased to match the stored normalised email, so this is still an index-usable
            // prefix-or-contains against a real column rather than a function over one.
            var term = query.Search.Trim().ToLowerInvariant();

            customers = customers.Where(customer =>
                customer.Email.Value.Contains(term) || customer.Name.Value.ToLower().Contains(term));
        }

        if (query.Status is not null)
        {
            customers = customers.Where(customer => customer.Status.Value == query.Status);
        }

        if (query.Role is not null)
        {
            customers = customers.Where(customer => customer.Role == query.Role);
        }

        if (query.EmailVerified is { } verified)
        {
            customers = verified
                ? customers.Where(customer => customer.EmailVerifiedAtUtc != null)
                : customers.Where(customer => customer.EmailVerifiedAtUtc == null);
        }

        // The keyset predicate. Email is unique, so there is no tie to break and no row can be
        // seen twice or skipped however much the table changes between requests.
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            var cursor = query.Cursor.Trim().ToLowerInvariant();
            customers = customers.Where(customer =>
                string.Compare(customer.Email.Value, cursor) > 0);
        }

        var page = await customers
            .OrderBy(customer => customer.Email.Value)
            .Take(query.Limit + 1)
            .Select(customer => new CustomerListItem(
                customer.Id,
                customer.Email.Value,
                customer.Name.Value,
                customer.Role,
                customer.Status.Value.ToString(),
                customer.EmailVerifiedAtUtc != null,
                // Counted by the database. Onyx A26 ran one extra round trip per customer here,
                // from inside a Select over an already-materialised list.
                customer.Addresses.Count,
                customer.RegisteredAtUtc))
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > query.Limit;
        var items = hasMore ? page[..query.Limit] : page;

        return new CustomerPage(items, hasMore ? items[^1].Email : null);
    }
}

internal sealed class GetAuthAttemptsQueryHandler(CustomersDbContext context)
    : IRequestHandler<GetAuthAttemptsQuery, ErrorOr<List<AuthAttemptResponse>>>
{
    private const int MaxLimit = 200;

    public async Task<ErrorOr<List<AuthAttemptResponse>>> Handle(
        GetAuthAttemptsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            return Error.Validation(
                "GetAuthAttempts.InvalidLimit",
                $"Limit must be between 1 and {MaxLimit}.");
        }

        return await context.AuthAttempts
            .AsNoTracking()
            .Where(attempt => attempt.CustomerId == query.CustomerId)
            // Ordered because it is capped — an unordered Take would return an arbitrary set
            // and call it "recent". That is the bug found in Catalog's own jobs; see
            // BUGS-AND-LESSONS §8, and note the EF warning for it is now an exception.
            .OrderByDescending(attempt => attempt.OccurredAtUtc)
            .Take(query.Limit)
            .Select(attempt => new AuthAttemptResponse(
                attempt.Outcome.ToString(),
                attempt.ClientFingerprint,
                attempt.OccurredAtUtc))
            .ToListAsync(cancellationToken);
    }
}
