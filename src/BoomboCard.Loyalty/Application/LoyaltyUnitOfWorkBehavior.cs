using BoomboCard.Loyalty.Contracts;
using BoomboCard.Loyalty.Data;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence;

namespace BoomboCard.Loyalty.Application;

/// <summary>
/// Loyalty's transaction boundary: <see cref="ModuleUnitOfWorkBehavior{TRequest,TResponse}"/>
/// closed over this module's context and constrained to its requests.
/// </summary>
internal sealed class LoyaltyUnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork<LoyaltyDbContext> unitOfWork,
    IDomainEventDispatcher<LoyaltyDbContext> dispatcher)
    : ModuleUnitOfWorkBehavior<TRequest, TResponse>(unitOfWork, dispatcher)
    where TRequest : notnull, ILoyaltyRequest;
