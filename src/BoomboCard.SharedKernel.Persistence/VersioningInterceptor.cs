using BoomboCard.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BoomboCard.SharedKernel.Persistence;

/// <summary>
/// Advances <see cref="AggregateRoot.Version"/> on every modified aggregate, immediately before
/// the write.
///
/// <para>
/// <b>Why the version must advance at all.</b> The concurrency check is "is the version still
/// the one I read". If the value never changed, that is always true and the token would detect
/// nothing:
/// </para>
/// <code>
/// Without the advance (version stays 7):
///   A: UPDATE SET Stock=1 WHERE Version=7  -> 1 row.  Version STILL 7.
///   B: UPDATE SET Stock=1 WHERE Version=7  -> STILL MATCHES. 1 row.
///   Both succeed. Four units sold from three. No error. (Onyx D1.)
///
/// With the advance:
///   A: UPDATE SET Stock=1, Version=8 WHERE Version=7  -> 1 row.
///   B: UPDATE SET Stock=1, Version=8 WHERE Version=7  -> 0 ROWS -> DbUpdateConcurrencyException
/// </code>
/// <para>
/// Reading captures a generation; writing requires that generation to still be current and then
/// advances it. The advance is what makes every other reader's captured value stale.
/// </para>
///
/// <para>
/// <b>Why it goes through EF's property API.</b> Mutating the POCO property directly is not
/// enough: <c>DetectChanges</c> has already run by the time a SavingChanges interceptor fires,
/// so EF never learns about the new value. The tracked CurrentValue stays stale and the
/// property is not marked modified, which produces an UPDATE whose SET and WHERE disagree —
/// zero rows affected, and a spurious DbUpdateConcurrencyException on a completely uncontended
/// write. Setting CurrentValue and IsModified explicitly leaves OriginalValue (the value read
/// from the database) for the WHERE clause and the new value for SET, which is exactly right.
/// </para>
///
/// <para>
/// Done centrally so it cannot be forgotten, and via an interceptor rather than a provider
/// feature (SQL Server <c>rowversion</c>, PostgreSQL <c>xmin</c>) so modules stay
/// provider-agnostic and the host chooses the database.
/// </para>
/// </summary>
public sealed class VersioningInterceptor : SaveChangesInterceptor
{
    private const string VersionProperty = nameof(AggregateRoot.Version);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Advance(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Advance(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Advance(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var roots = context.ChangeTracker.Entries<AggregateRoot>().ToList();

        foreach (var entry in roots)
        {
            // A brand new aggregate starts at version 0. Nothing to advance.
            if (entry.State is EntityState.Added or EntityState.Detached)
            {
                continue;
            }

            if (!HasChangesInsideAggregate(entry, context))
            {
                continue;
            }

            var property = entry.Property<uint>(VersionProperty);

            // The value the database currently holds. This is what must end up in the WHERE
            // clause, so capture it before touching anything.
            var original = property.OriginalValue;

            property.CurrentValue = original + 1;

            // On an otherwise-Unchanged root this promotes the row to Modified, so the new
            // version actually gets written — the case where only a child entity or an owned
            // value object changed.
            property.IsModified = true;

            // Pinned explicitly, so the WHERE clause is guaranteed to carry the value the
            // database actually holds regardless of how the entry was promoted to Modified.
            property.OriginalValue = original;

            var next = original + 1;

            // Keep the in-memory aggregate consistent with what is about to be written, so a
            // caller reading Version after the save sees the committed value.
            DomainEventAccess.SetVersion(entry.Entity, next);
        }
    }

    /// <summary>
    /// Whether anything inside the aggregate boundary changed — the root's own scalars, an owned
    /// value object, or a child entity.
    ///
    /// Checking only <c>entry.State is Modified</c> is not enough, and getting that wrong is a
    /// silent correctness hole rather than a visible bug. <c>Product.Stock</c> and
    /// <c>Product.Status</c> are owned types, so EF marks the OWNED entry Modified and leaves
    /// the Product entry Unchanged. A stock deduction would then never advance the version — and
    /// a concurrency token that never advances detects nothing, which is exactly the Onyx D1
    /// oversell with extra ceremony. A test caught this.
    ///
    /// Treating a child change as a change to the root is also the correct aggregate semantic:
    /// the aggregate is one consistency boundary, so two concurrent price changes to the same
    /// product should conflict.
    /// </summary>
    private static bool HasChangesInsideAggregate(EntityEntry rootEntry, DbContext context)
    {
        if (rootEntry.State is EntityState.Modified)
        {
            return true;
        }

        // Owned value objects: Product.Stock, Product.Status, Product.CostPrice.
        foreach (var reference in rootEntry.References)
        {
            if (reference.TargetEntry is { } target
                && target.Metadata.IsOwned()
                && target.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                return true;
            }
        }

        // Child entities inside the aggregate: Product.Prices.
        foreach (var collection in rootEntry.Collections)
        {
            if (collection.CurrentValue is null)
            {
                continue;
            }

            foreach (var child in collection.CurrentValue)
            {
                if (context.Entry(child).State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
