using BoomboCard.Catalog.Domain.Exports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Catalog.Data.Configuration;

internal sealed class ProductExportConfiguration : IEntityTypeConfiguration<ProductExport>
{
    public void Configure(EntityTypeBuilder<ProductExport> builder)
    {
        builder.ToTable("ProductExports");

        builder.HasKey(export => export.Id);

        // Same concurrency token as Product. Here it does a second job: it makes claiming an
        // export atomic, so two generator instances cannot both pick up the same one.
        builder.Property(export => export.Version).IsConcurrencyToken().IsRequired();

        builder.Property(export => export.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(export => export.RequestedBy).HasMaxLength(200).IsRequired();
        builder.Property(export => export.RequestedAtUtc).IsRequired();
        builder.Property(export => export.FileName).HasMaxLength(200);
        builder.Property(export => export.StoragePath).HasMaxLength(1000);
        builder.Property(export => export.Error).HasMaxLength(2000);

        // The generator's only query: requested exports, oldest first.
        builder.HasIndex(export => new { export.Status, export.RequestedAtUtc })
            .HasDatabaseName("IX_ProductExports_Pending");

        // The reaper's query: ready exports past their expiry.
        builder.HasIndex(export => export.ExpiresAtUtc)
            .HasDatabaseName("IX_ProductExports_Expiry");
    }
}
