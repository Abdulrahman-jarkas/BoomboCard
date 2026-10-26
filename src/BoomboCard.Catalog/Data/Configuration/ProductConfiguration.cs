using BoomboCard.Catalog.Domain.ProductAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Catalog.Data.Configuration;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(product => product.Id);

        // The optimistic concurrency token. VersioningInterceptor increments it on save; EF
        // puts the ORIGINAL value in the UPDATE predicate, so a concurrent writer affects zero
        // rows and raises DbUpdateConcurrencyException instead of overwriting (Onyx D1).
        builder.Property(product => product.Version)
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(product => product.Name)
            .HasMaxLength(Product.MaxNameLength)
            .IsRequired();

        builder.Property(product => product.LowStockThreshold).IsRequired();

        // Sku is mapped as an OWNED TYPE, not with a value converter.
        //
        // A converter would be fewer moving parts, but it makes Sku unqueryable: with a
        // converter, `product.Sku.Value` is a member access on a CLR object rather than a
        // column, so EF cannot translate it. It survives in a final Select (evaluated on the
        // client) but fails outright in OrderBy, Contains or a keyset predicate — which is
        // every query the catalogue actually needs. The export and the paged search both broke
        // on exactly that.
        //
        // As an owned type sharing this table, Sku_Value IS a real column: orderable,
        // filterable, and indexable.
        //
        // The rule this settles: value converters for single values you only ever read back;
        // owned types for anything you query, sort or index.
        builder.OwnsOne(product => product.Sku, sku =>
        {
            sku.Property(value => value.Value)
                .HasColumnName("Sku")
                .HasMaxLength(Sku.MaxLength)
                .IsRequired();

            // Onyx D10: nothing enforced SKU uniqueness, so CSV import duplicated the whole
            // catalogue on every re-import. Combined with Sku normalising to upper case,
            // "ONX-001" and "onx-001" are the same value and collide.
            sku.HasIndex(value => value.Value)
                .IsUnique()
                .HasDatabaseName("UX_Products_Sku");
        });
        builder.Navigation(product => product.Sku).IsRequired();

        builder.OwnsOne(product => product.Status, status =>
        {
            status.Property(value => value.Value)
                .HasColumnName("Status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
        });
        builder.Navigation(product => product.Status).IsRequired();

        builder.OwnsOne(product => product.Stock, stock =>
        {
            stock.Property(level => level.Quantity)
                .HasColumnName("StockQuantity")
                .IsRequired();
        });
        builder.Navigation(product => product.Stock).IsRequired();

        builder.OwnsOne(product => product.CostPrice, money =>
        {
            money.Property(value => value.Amount).HasColumnName("CostPriceAmount");
            money.Property(value => value.CurrencyCode)
                .HasColumnName("CostPriceCurrency")
                .HasMaxLength(3);
        });

        builder.HasMany(product => product.Prices)
            .WithOne()
            .HasForeignKey(price => price.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata
            .FindNavigation(nameof(Product.Prices))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

    }
}
