using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Data;
using BoomboCard.Customers.Data.Repositories;
using BoomboCard.Customers.Domain.CustomerAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Customers.Application.Customers.Commands;

/// <summary>
/// Grants or revokes a role.
///
/// <para>
/// Note what is <b>not</b> a parameter: who is making the change. It comes from
/// <see cref="ICurrentActor"/>, read from the authenticated principal, because an actor the
/// caller supplies is an actor the caller can forge — and the only reason to record an actor at
/// all is to be able to trust it afterwards.
/// </para>
///
/// <para>
/// Three independent things have to hold for this to be safe, and all three exist: the endpoint
/// requires the <c>Admin</c> policy; the aggregate refuses a change where the actor is the
/// subject (<c>Customer.ChangeRole</c>); and the resulting fact is published with the actor
/// attached. Onyx had none of the three — S3 meant any shopper could do it through a profile
/// update, and A40 meant nothing recorded that they had.
/// </para>
/// </summary>
public sealed record ChangeCustomerRoleCommand(Guid CustomerId, CustomerRole Role)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class ChangeCustomerRoleCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    ICurrentActor actor,
    TimeProvider timeProvider,
    ILogger<ChangeCustomerRoleCommandHandler> logger)
    : IRequestHandler<ChangeCustomerRoleCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ChangeCustomerRoleCommand command,
        CancellationToken cancellationToken)
    {
        if (actor.CustomerId is not { } actorId)
        {
            return CustomerErrors.ActorRequired;
        }

        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        var changed = customer.ChangeRole(command.Role, actorId, timeProvider.GetUtcNow());

        if (changed.IsError)
        {
            return changed.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Role of {CustomerId} changed to {Role} by {ActorId}.",
            customer.Id,
            command.Role,
            actorId);

        return Result.Success;
    }
}

/// <summary>
/// Suspends an account: it keeps its history and its credential but cannot sign in or transact.
/// Reversible, unlike anonymisation.
/// </summary>
public sealed record SuspendCustomerCommand(Guid CustomerId)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class SuspendCustomerCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<SuspendCustomerCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        SuspendCustomerCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        var suspended = customer.Suspend(timeProvider.GetUtcNow());

        if (suspended.IsError)
        {
            return suspended.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

public sealed record ReinstateCustomerCommand(Guid CustomerId)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class ReinstateCustomerCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<ReinstateCustomerCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ReinstateCustomerCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        var reinstated = customer.Reinstate(timeProvider.GetUtcNow());

        if (reinstated.IsError)
        {
            return reinstated.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// Erases a customer's personal data, keeping the row and therefore the financial record.
///
/// <para>
/// This is what Onyx's <c>DELETE /api/admin/customers/{id}</c> should have been. That endpoint
/// called <c>_context.Customers.Remove(customer)</c> on a row with orders and payments hanging
/// off it (D13) — so it either threw on a foreign key or orphaned the entire order history —
/// and it carried no authorization attribute at all, so it was reachable anonymously (S2) while
/// the PR claimed every admin endpoint was behind the Admin role (X14).
/// </para>
///
/// <para>
/// The event it publishes is the half that a delete statement cannot do: other modules hold
/// copies of the name and address — a notification log, a reporting projection — and they have
/// to erase theirs too.
/// </para>
/// </summary>
public sealed record AnonymiseCustomerCommand(Guid CustomerId)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class AnonymiseCustomerCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    TimeProvider timeProvider,
    ILogger<AnonymiseCustomerCommandHandler> logger)
    : IRequestHandler<AnonymiseCustomerCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        AnonymiseCustomerCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        var anonymised = customer.Anonymise(timeProvider.GetUtcNow());

        if (anonymised.IsError)
        {
            return anonymised.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Customer {CustomerId} anonymised. This is not reversible.", customer.Id);

        return Result.Success;
    }
}
