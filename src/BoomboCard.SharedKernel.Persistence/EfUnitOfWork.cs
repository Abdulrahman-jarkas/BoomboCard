using BoomboCard.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BoomboCard.SharedKernel.Persistence;

public sealed class EfUnitOfWork<TContext>(
    TContext context,
    IDomainEventDispatcher<TContext> dispatcher) : IUnitOfWork<TContext>
    where TContext : DbContext
{
    public bool HasActiveTransaction => context.Database.CurrentTransaction is not null;

    public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        return new EfTransaction(transaction);
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // The guard. See IUnitOfWork.SaveChangesAsync for the bug it closes.
        //
        // Inside a command the behaviour has opened a transaction and will drain events after
        // this returns, so the check is skipped. Outside one, nothing will — and dropping events
        // is invisible, which makes it exactly the kind of failure this codebase exists to turn
        // into a loud one.
        if (!HasActiveTransaction)
        {
            var pending = context.ChangeTracker
                .Entries<AggregateRoot>()
                .Where(entry => entry.Entity.DomainEvents.Count > 0)
                .Select(entry => entry.Entity.GetType().Name)
                .Distinct()
                .ToList();

            if (pending.Count > 0)
            {
                throw new InvalidOperationException(
                    $"SaveChangesAsync was called on {typeof(TContext).Name} with no active "
                    + "transaction while these aggregates hold undispatched domain events: "
                    + string.Join(", ", pending)
                    + ". The events would be written to nothing and then dropped when the scope "
                    + "ends, so any outbox translator for them would silently never fire. Call "
                    + "SaveAndDispatchAsync instead, or dispatch a command so the pipeline owns "
                    + "the transaction.");
            }
        }

        return await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> SaveAndDispatchAsync(CancellationToken cancellationToken = default)
    {
        // Already inside a command's transaction: save and let the behaviour drain, exactly as
        // a handler would. Draining here as well would publish events the behaviour is about to
        // publish, from inside a transaction it owns.
        if (HasActiveTransaction)
        {
            return await context.SaveChangesAsync(cancellationToken);
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);

        var written = await context.SaveChangesAsync(cancellationToken);

        // Same ordering as the pipeline behaviour: handlers observe persisted state, and their
        // own writes commit or roll back together with ours.
        await dispatcher.DispatchUntilDrainedAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return written;
    }

    /// <summary>
    /// Detaches everything, which also drops any pending domain events with it — the dispatcher
    /// finds the aggregates through the change tracker, so a cleared tracker means nothing to
    /// publish.
    /// </summary>
    public void DiscardChanges() => context.ChangeTracker.Clear();

    private sealed class EfTransaction(IDbContextTransaction transaction) : ITransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            transaction.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
