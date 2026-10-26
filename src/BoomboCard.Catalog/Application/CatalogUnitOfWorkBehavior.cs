using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence;

namespace BoomboCard.Catalog.Application;

/// <summary>
/// Catalog's transaction boundary: <see cref="ModuleUnitOfWorkBehavior{TRequest,TResponse}"/>
/// closed over Catalog's context and constrained to Catalog's requests.
///
/// Four lines of wiring, and they are the reason a Sales command cannot accidentally be
/// committed through Catalog's connection. See <see cref="IUnitOfWork{TScope}"/> for the bug
/// this shape exists to make impossible.
/// </summary>
internal sealed class CatalogUnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork<CatalogDbContext> unitOfWork,
    IDomainEventDispatcher<CatalogDbContext> dispatcher)
    : ModuleUnitOfWorkBehavior<TRequest, TResponse>(unitOfWork, dispatcher)
    where TRequest : notnull, ICatalogRequest;
