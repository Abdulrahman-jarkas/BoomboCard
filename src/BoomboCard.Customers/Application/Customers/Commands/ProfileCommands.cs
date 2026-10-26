using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Data;
using BoomboCard.Customers.Data.Repositories;
using BoomboCard.Customers.Domain.CustomerAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Customers.Application.Customers.Commands;

/// <summary>
/// Updates the customer's own profile.
///
/// <para>
/// <b>One field.</b> That is the whole fix for Onyx S3, and it is worth being explicit about why
/// a narrow contract beats a careful handler. Onyx's equivalent was
/// <c>Update(int id, [FromBody] Customer updated)</c> followed by
/// <c>Entry(existing).CurrentValues.SetValues(updated)</c> — so the request could carry
/// <c>Role</c>, <c>PasswordHash</c>, <c>LoyaltyPoints</c> and <c>CreditLimit</c>, and every one
/// of them was written. No amount of validation inside that handler would have been safe,
/// because the next property added to the entity would have been silently assignable too.
/// </para>
///
/// <para>
/// There is no email field here either. Changing an address invalidates a verification that was
/// a fact about a specific inbox, so it needs its own flow — issue a token to the <i>new</i>
/// address, and only move the account once that token comes back. That is a real capability and
/// it is deliberately not in this phase; see <c>ROADMAP.md</c>. A half-built version that
/// changed the address and left <c>EmailVerifiedAtUtc</c> standing would be worse than its
/// absence.
/// </para>
/// </summary>
public sealed record UpdateProfileCommand(Guid CustomerId, string Name)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class UpdateProfileCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<UpdateProfileCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateProfileCommand command,
        CancellationToken cancellationToken)
    {
        var name = PersonName.Create(command.Name);

        if (name.IsError)
        {
            return name.Errors;
        }

        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        var renamed = customer.Rename(name.Value, timeProvider.GetUtcNow());

        if (renamed.IsError)
        {
            return renamed.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}
