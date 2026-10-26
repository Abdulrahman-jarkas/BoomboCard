using BoomboCard.Customers.Domain.CustomerAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Customers.Data.Configuration;

internal sealed class AddressConfiguration : IEntityTypeConfiguration<Address>
{
    public void Configure(EntityTypeBuilder<Address> builder)
    {
        builder.ToTable("Addresses");

        builder.HasKey(address => address.Id);

        builder.Property(address => address.CustomerId).IsRequired();

        builder.Property(address => address.Label)
            .HasMaxLength(Address.MaxLabelLength)
            .IsRequired();

        builder.Property(address => address.Line1)
            .HasMaxLength(Address.MaxLineLength)
            .IsRequired();

        builder.Property(address => address.Line2).HasMaxLength(Address.MaxLineLength);

        builder.Property(address => address.City)
            .HasMaxLength(Address.MaxLineLength)
            .IsRequired();

        builder.Property(address => address.PostalCode).HasMaxLength(Address.MaxLabelLength);

        builder.Property(address => address.CountryCode)
            .HasMaxLength(2)
            .IsRequired();

        // Note there is no IsDefault column and no filtered unique index enforcing one.
        //
        // The first attempt here was a boolean on this table plus
        // `.HasFilter("[IsDefault] = 1")`, which is SQL Server syntax — a provider leaking into
        // a module whose whole seam is that it references no provider. Making the filter
        // conditional on the provider name would have worked, the way the DateTimeOffset
        // converter does, but it is the wrong trade for a constraint: that converter closes a
        // capability gap, whereas this would have meant the invariant is enforced on some
        // databases and not others.
        //
        // So the default moved to `Customers.DefaultAddressId` instead. One column cannot hold
        // two values, so "at most one default per customer" stops being a constraint to check
        // and becomes a shape that cannot represent the violation. No index, no provider
        // syntax, and nothing to get wrong. See Customer.SetDefaultAddress.
        builder.HasIndex(address => address.CustomerId)
            .HasDatabaseName("IX_Addresses_CustomerId");
    }
}
