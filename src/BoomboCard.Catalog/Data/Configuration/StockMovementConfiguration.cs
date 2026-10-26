using BoomboCard.Catalog.Domain.StockMovements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Catalog.Data.Configuration;

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("StockMovements");

        builder.HasKey(movement => movement.Id);

        builder.Property(movement => movement.ProductId).IsRequired();
        builder.Property(movement => movement.Delta).IsRequired();
        builder.Property(movement => movement.QuantityAfter).IsRequired();
        builder.Property(movement => movement.OccurredAtUtc).IsRequired();

        builder.Property(movement => movement.Reason)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(movement => movement.Reference)
            .HasMaxLength(100)
            .IsRequired();

        // THE idempotency guarantee.
        //
        // Outbox delivery is at-least-once, so "restore stock for session X" will sometimes
        // arrive twice. This unique index makes the second attempt fail at the database rather
        // than inflate stock, which is why RestoreStockCommandHandler can be a plain no-op on
        // a duplicate.
        //
        // Onyx M5: three payment attempts with no idempotency key at all, so a gateway timeout
        // followed by a retry could charge a customer twice — and the PR asserted it was safe
        // because "the gateway call is idempotent on their end", with nothing in the request
        // identifying the payment.
        builder.HasIndex(movement => new { movement.ProductId, movement.Reference, movement.Reason })
            .IsUnique()
            .HasDatabaseName("UX_StockMovements_Idempotency");

        // For the orphan-reconciliation sweep: Sale movements with no matching restore that
        // never reached a confirmed order.
        builder.HasIndex(movement => new { movement.Reason, movement.OccurredAtUtc })
            .HasDatabaseName("IX_StockMovements_Reconciliation");

        builder.HasIndex(movement => movement.Reference)
            .HasDatabaseName("IX_StockMovements_Reference");
    }
}
