using BoomboCard.Sales.Contracts;
using BoomboCard.Sales.Data;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence;

namespace BoomboCard.Sales.Application;

/// <summary>
/// Sales' transaction boundary: <see cref="ModuleUnitOfWorkBehavior{TRequest,TResponse}"/>
/// closed over this module's context and constrained to its requests.
///
/// <para>
/// Sales is the module where the per-module typing earns its keep. A confirm handler calls into
/// Catalog, Promotions and Loyalty, and each of those runs its own behaviour on its own context.
/// If any of the four were registered against the bare <c>IUnitOfWork</c>, the innermost one
/// resolved would commit — or roll back — somebody else's work. That is bug 7, and it is why
/// nothing in this system is registered against an untyped persistence interface.
/// </para>
/// </summary>
internal sealed class SalesUnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork<SalesDbContext> unitOfWork,
    IDomainEventDispatcher<SalesDbContext> dispatcher)
    : ModuleUnitOfWorkBehavior<TRequest, TResponse>(unitOfWork, dispatcher)
    where TRequest : notnull, ISalesRequest;
