using BoomboCard.Catalog.Domain.ProductAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Catalog.Data.Configuration;

internal sealed class ProductPriceConfiguration : IEntityTypeConfiguration<ProductPrice>
{
    public void Configure(EntityTypeBuilder<ProductPrice> builder)
    {
        builder.ToTable("ProductPrices");

        builder.HasKey(price => price.Id);

        builder.Property(price => price.CountryCode).HasMaxLength(2).IsRequired();

        builder.Property(price => price.EffectiveFrom).IsRequired();
        builder.Property(price => price.EffectiveTo);

        builder.OwnsOne(price => price.Amount, money =>
        {
            money.Property(value => value.Amount).HasColumnName("Amount").IsRequired();
            money.Property(value => value.CurrencyCode)
                .HasColumnName("CurrencyCode")
                .HasMaxLength(3)
                .IsRequired();
        });
        builder.Navigation(price => price.Amount).IsRequired();

        // Resolving "the price for this country right now" is the hottest read in the module.
        builder.HasIndex(price => new { price.ProductId, price.CountryCode, price.EffectiveFrom })
            .HasDatabaseName("IX_ProductPrices_Lookup");
    }
}
