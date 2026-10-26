using BoomboCard.Promotions.Contracts;
using BoomboCard.Promotions.Data;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence;

namespace BoomboCard.Promotions.Application;

/// <summary>
/// Promotions' transaction boundary: <see cref="ModuleUnitOfWorkBehavior{TRequest,TResponse}"/>
/// closed over this module's context and constrained to its requests.
///
/// See <see cref="IUnitOfWork{TScope}"/> for the bug this shape makes impossible.
/// </summary>
internal sealed class PromotionsUnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork<PromotionsDbContext> unitOfWork,
    IDomainEventDispatcher<PromotionsDbContext> dispatcher)
    : ModuleUnitOfWorkBehavior<TRequest, TResponse>(unitOfWork, dispatcher)
    where TRequest : notnull, IPromotionsRequest;
