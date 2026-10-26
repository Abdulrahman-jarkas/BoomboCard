using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BoomboCard.SharedKernel.Persistence;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Declares every Guid primary key as client-generated.
    ///
    /// <para>
    /// <b>Why this is not optional.</b> Our domain assigns ids itself — <c>Guid.NewGuid()</c> in
    /// a factory — so that an aggregate can be fully formed and raise events carrying its real id
    /// before the database is involved.
    /// </para>
    ///
    /// <para>
    /// Without this, EF uses the key value to guess the state of an untracked entity it
    /// discovers through a navigation: a non-default key means "this row already exists", so a
    /// brand new child added to a TRACKED parent's collection is marked <c>Modified</c> instead
    /// of <c>Added</c>. EF then emits an UPDATE for a row that was never inserted, it affects
    /// zero rows, and the failure surfaces as a <c>DbUpdateConcurrencyException</c> — which
    /// looks exactly like a lost concurrency race and is nothing of the kind.
    /// </para>
    ///
    /// <para>
    /// It only bites on the path where the parent is already tracked. An aggregate saved via
    /// <c>Add()</c> has its whole graph marked Added explicitly, so that path works and hides
    /// the problem. Here it meant <c>SetPrice</c> on an existing product failed every time while
    /// creating a product with prices succeeded.
    /// </para>
    ///
    /// <para>
    /// Applied across the whole model rather than per entity, so a new aggregate cannot be added
    /// without it.
    /// </para>
    /// </summary>
    public static ModelBuilder UseClientGeneratedKeys(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var primaryKey = entityType.FindPrimaryKey();

            if (primaryKey is null)
            {
                continue;
            }

            foreach (var property in primaryKey.Properties.Where(p => p.ClrType == typeof(Guid)))
            {
                property.ValueGenerated = ValueGenerated.Never;
            }
        }

        return modelBuilder;
    }
}
