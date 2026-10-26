namespace BoomboCard.SharedKernel;

/// <summary>
/// One unit of work per bounded context, implemented over that context's DbContext.
/// Lives here rather than in Persistence so ModuleUnitOfWorkBehavior can stay
/// provider-agnostic and take only two generic parameters, which is what MediatR's
/// open-generic pipeline registration requires.
///
/// <para>
/// <b>Nothing is registered against this interface.</b> It exists so the pipeline behaviour
/// has something to depend on; every actual registration is against
/// <see cref="IUnitOfWork{TScope}"/>. See that type for why.
/// </para>
/// </summary>
public interface IUnitOfWork
{
    bool HasActiveTransaction { get; }

    Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes pending changes. <b>For use inside a command</b>, where
    /// <see cref="ModuleUnitOfWorkBehavior{TRequest,TResponse}"/> owns the transaction and will
    /// drain domain events afterwards.
    ///
    /// <para>
    /// Calling this with <b>no active transaction</b> while aggregates hold undispatched domain
    /// events <b>throws</b>. That guard exists because the alternative is silent: a background
    /// job that mutates an aggregate and calls this directly writes the rows, raises the events,
    /// and then drops them when the scope ends — so an outbox translator added later never
    /// fires, with no exception and no log. Catalog's export generator did exactly this for
    /// weeks, harmlessly, because nothing yet subscribed to its events. Use
    /// <see cref="SaveAndDispatchAsync"/> outside a command.
    /// </para>
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes pending changes, publishes the domain events they raised, and commits — the same
    /// save → publish → re-save → commit sequence a command gets, for callers that are not
    /// commands.
    ///
    /// <para>
    /// Background jobs are the callers that need this. A job is not dispatched through the
    /// MediatR pipeline, so nothing has opened a transaction for it and nothing will drain its
    /// events; without this it would have to re-implement the behaviour's sequence, and the
    /// usual outcome of re-implementing a guarantee is implementing most of it.
    /// </para>
    ///
    /// <para>
    /// Joins an existing transaction rather than nesting if one is somehow already open, so it
    /// is safe even if a future caller is inside a command after all.
    /// </para>
    /// </summary>
    Task<int> SaveAndDispatchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Abandons every pending in-memory change, so nothing is written and no domain events are
    /// dispatched.
    ///
    /// Needed because simply not calling <see cref="SaveChangesAsync"/> is NOT enough. The
    /// domain event dispatcher saves whatever handlers changed, and its check is
    /// "does the change tracker have anything pending" — so a dry run that built aggregates and
    /// left them tracked got silently committed by the dispatcher instead. That was a real bug,
    /// found by the import dry run reporting committed:false while writing three products.
    /// </summary>
    void DiscardChanges();
}

/// <summary>
/// One module's unit of work, identified by that module's own <c>DbContext</c> type.
///
/// <para>
/// This generic parameter is the whole point. <see cref="IUnitOfWork"/> used to be registered
/// unkeyed, once per module, by <c>AddModulePersistence</c>. With one module that worked. With
/// two, the last registration won for every handler in the process: a Catalog handler would be
/// handed the Sales unit of work, call <c>SaveChangesAsync</c> on the Sales context, write
/// nothing, and the behaviour would then commit a transaction over a context with no changes.
/// No exception, no log, no rows — the exact shape of failure this codebase exists to refuse.
/// </para>
///
/// <para>
/// Typing it on the context makes that unrepresentable: <c>IUnitOfWork&lt;CatalogDbContext&gt;</c>
/// resolves Catalog's and nothing else, and asking for a context the module cannot see does not
/// compile. Keyed services were the alternative and are worse — a forgotten
/// <c>[FromKeyedServices("catalog")]</c> silently resolves the wrong module, which is the
/// failure mode being removed rather than relocated.
/// </para>
///
/// <para>
/// <typeparamref name="TScope"/> is unconstrained because this project has no EF Core
/// reference. Persistence constrains its implementation to <c>DbContext</c>.
/// </para>
/// </summary>
public interface IUnitOfWork<TScope> : IUnitOfWork;

public interface ITransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}
