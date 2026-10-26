using BoomboCard.SharedKernel;
using MediatR;

namespace BoomboCard.SharedKernel.Persistence;

/// <summary>
/// Gives every state-changing command exactly one transaction, spanning the handler's own
/// writes and everything its domain event handlers do.
///
/// This is the piece all three reference implementations are missing:
///  - Talabat scatters ModuleTransactionScope.Create() across individual handlers, so whether
///    a command is transactional depends on whether its author remembered.
///  - dome-gym has a transaction, but opens it in middleware for every request including
///    reads, and commits it in Response.OnCompleted — after the client already has its 200.
///  - RiverBooks has no transaction at all: the aggregate is autocommitted before any handler
///    runs, so a failing handler leaves the write standing and the caller seeing a 500.
///
/// Here the transaction opens before the handler and commits after events are drained, so the
/// response always describes what was actually committed.
///
/// <para>
/// <b>Abstract, and closed once per module.</b> MediatR's open-generic pipeline registration
/// permits exactly two type parameters, so the module cannot be a third one — and the unit of
/// work this behaviour needs is per module. Each module therefore declares a sealed subclass
/// constrained to its own request marker:
/// </para>
///
/// <code>
/// internal sealed class CatalogUnitOfWorkBehavior&lt;TRequest, TResponse&gt;(
///     IUnitOfWork&lt;CatalogDbContext&gt; unitOfWork,
///     IDomainEventDispatcher&lt;CatalogDbContext&gt; dispatcher)
///     : ModuleUnitOfWorkBehavior&lt;TRequest, TResponse&gt;(unitOfWork, dispatcher)
///     where TRequest : notnull, ICatalogRequest;
/// </code>
///
/// <para>
/// The constraint is what does the routing: the container skips an open-generic registration
/// whose constraints the closed type would violate, so a Sales command never picks up Catalog's
/// transaction. The cost is that a command carrying no module marker would quietly get no
/// transaction at all — so <c>ModuleWiringTests</c> asserts that every
/// <see cref="ITransactionalRequest"/> carries exactly one marker and every marker has a
/// registered behaviour. An unenforced convention here would be a silent loss of atomicity,
/// which is Onyx D3 arriving by a different road.
/// </para>
/// </summary>
public abstract class ModuleUnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork unitOfWork,
    IDomainEventDispatcher dispatcher)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, ITransactionalRequest
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // A command dispatched from inside another command's transaction joins it rather than
        // nesting. Nested transactions on one connection are not supported, and silently
        // swallowing that is how you end up with partially committed work.
        //
        // Note this is per module: a Sales command that dispatches a Catalog command gets a
        // second transaction, on Catalog's own context and connection, because the two modules
        // own separate databases as far as the code is concerned. That is deliberate — an
        // invariant never crosses a boundary, so the two commits are sequenced and made
        // recoverable by the checkout session and the expiry sweeper, not welded together.
        if (unitOfWork.HasActiveTransaction)
        {
            return await next();
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var response = await next();

        await dispatcher.DispatchUntilDrainedAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return response;
    }
}
