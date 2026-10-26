using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Data;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence;

namespace BoomboCard.Customers.Application;

/// <summary>
/// Customers' transaction boundary: <see cref="ModuleUnitOfWorkBehavior{TRequest,TResponse}"/>
/// closed over Customers' context and constrained to Customers' requests.
///
/// Four lines, and they are what keeps a Catalog or Sales command from being committed through
/// this module's connection. See <see cref="IUnitOfWork{TScope}"/>.
/// </summary>
internal sealed class CustomersUnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork<CustomersDbContext> unitOfWork,
    IDomainEventDispatcher<CustomersDbContext> dispatcher)
    : ModuleUnitOfWorkBehavior<TRequest, TResponse>(unitOfWork, dispatcher)
    where TRequest : notnull, ICustomersRequest;
