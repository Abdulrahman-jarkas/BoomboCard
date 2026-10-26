using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Data;
using BoomboCard.Customers.Data.Repositories;
using BoomboCard.Customers.Domain.CustomerAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Customers.Application.Customers.Commands;

/// <summary>
/// Adds an address. Becomes the default if asked, or automatically if it is the first.
/// </summary>
public sealed record AddAddressCommand(
    Guid CustomerId,
    string Label,
    string Line1,
    string? Line2,
    string City,
    string? PostalCode,
    string CountryCode,
    bool MakeDefault = false) : IRequest<ErrorOr<Guid>>, ICustomersRequest;

internal sealed class AddAddressCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<AddAddressCommand, ErrorOr<Guid>>
{
    public async Task<ErrorOr<Guid>> Handle(
        AddAddressCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        var added = customer.AddAddress(
            command.Label,
            command.Line1,
            command.Line2,
            command.City,
            command.PostalCode,
            command.CountryCode,
            command.MakeDefault,
            timeProvider.GetUtcNow());

        if (added.IsError)
        {
            return added.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return added.Value;
    }
}

public sealed record UpdateAddressCommand(
    Guid CustomerId,
    Guid AddressId,
    string Label,
    string Line1,
    string? Line2,
    string City,
    string? PostalCode,
    string CountryCode) : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class UpdateAddressCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<UpdateAddressCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateAddressCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        // The address id is looked up inside the customer's own collection, so an id belonging
        // to someone else is a 404 rather than an edit. The endpoint's SelfOrAdmin policy
        // already established whose account this is; this is the same rule one layer in, so it
        // holds for any future caller that is not an endpoint.
        var updated = customer.UpdateAddress(
            command.AddressId,
            command.Label,
            command.Line1,
            command.Line2,
            command.City,
            command.PostalCode,
            command.CountryCode,
            timeProvider.GetUtcNow());

        if (updated.IsError)
        {
            return updated.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

public sealed record RemoveAddressCommand(Guid CustomerId, Guid AddressId)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class RemoveAddressCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<RemoveAddressCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RemoveAddressCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        var removed = customer.RemoveAddress(command.AddressId, timeProvider.GetUtcNow());

        if (removed.IsError)
        {
            return removed.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

public sealed record SetDefaultAddressCommand(Guid CustomerId, Guid AddressId)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class SetDefaultAddressCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<SetDefaultAddressCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        SetDefaultAddressCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        var set = customer.SetDefaultAddress(command.AddressId, timeProvider.GetUtcNow());

        if (set.IsError)
        {
            return set.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}
