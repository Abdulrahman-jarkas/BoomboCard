using BoomboCard.Customers.Domain.CustomerAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Customers.Data.Configuration;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");

        builder.HasKey(customer => customer.Id);

        // The optimistic concurrency token. It is what makes a single-use reset token actually
        // single-use and a lockout counter actually monotonic under concurrent sign-ins.
        builder.Property(customer => customer.Version)
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(customer => customer.Role)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(customer => customer.RegisteredAtUtc).IsRequired();

        // No foreign key to Addresses, deliberately. A constraint in both directions is a
        // circular reference the provider has to be coaxed through on insert, and it buys
        // nothing here: this is a reference WITHIN an aggregate, so there is exactly one writer,
        // it always loads the whole address collection, and Customer.RemoveAddress is the only
        // place a row disappears — which is why it clears this when it does.
        builder.Property(customer => customer.DefaultAddressId);

        // Email is an OWNED TYPE, not a value converter — the rule Catalog settled the hard way
        // (BUGS-AND-LESSONS §3): converters for single values you only read back, owned types
        // for anything you query, sort or index. Login looks up by email on every attempt, so
        // this has to be a real column the provider can match server-side.
        builder.OwnsOne(customer => customer.Email, email =>
        {
            email.Property(value => value.Value)
                .HasColumnName("Email")
                .HasMaxLength(EmailAddress.MaxLength)
                .IsRequired();

            // Onyx D10: no unique constraint on any natural key, so two accounts could hold the
            // same address and the login lookup's FirstOrDefault would silently pick one.
            // Combined with EmailAddress normalising to lower case, "Ada@x.com" and "ada@x.com"
            // are the same value and collide here rather than becoming two accounts.
            //
            // It is also what makes the SQL injection impossible to reintroduce by convenience:
            // the reason Onyx reached for raw SQL (S1) was COLLATE NOCASE for case-insensitive
            // matching, and a normalised column needs no collation trick.
            email.HasIndex(value => value.Value)
                .IsUnique()
                .HasDatabaseName("UX_Customers_Email");
        });
        builder.Navigation(customer => customer.Email).IsRequired();

        builder.OwnsOne(customer => customer.Name, name =>
        {
            name.Property(value => value.Value)
                .HasColumnName("Name")
                .HasMaxLength(PersonName.MaxLength)
                .IsRequired();
        });
        builder.Navigation(customer => customer.Name).IsRequired();

        builder.OwnsOne(customer => customer.Status, status =>
        {
            status.Property(value => value.Value)
                .HasColumnName("Status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
        });
        builder.Navigation(customer => customer.Status).IsRequired();

        builder.OwnsOne(customer => customer.Credential, credential =>
        {
            // No index, on purpose. Nothing ever looks a customer up by their password hash,
            // and an index would be a convenient lookup table for identical passwords.
            credential.Property(value => value.Verifier)
                .HasColumnName("PasswordVerifier")
                .HasMaxLength(500)
                .IsRequired();

            credential.Property(value => value.UpdatedAtUtc)
                .HasColumnName("PasswordUpdatedAtUtc")
                .IsRequired();

            credential.Property(value => value.FailedAttemptCount)
                .HasColumnName("FailedSignInCount")
                .IsRequired();

            credential.Property(value => value.LockedOutUntilUtc)
                .HasColumnName("LockedOutUntilUtc");
        });
        builder.Navigation(customer => customer.Credential).IsRequired();

        builder.HasMany(customer => customer.Addresses)
            .WithOne()
            .HasForeignKey(address => address.CustomerId)
            // Cascade, because an address has no meaning without its customer and erasure
            // overwrites rather than deletes. Contrast Onyx D13, which cascaded a hard DELETE
            // from a customer into their orders and payments — destroying the financial record.
            // Orders live in another module and another database here, so no cascade can reach
            // them even by accident.
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata
            .FindNavigation(nameof(Customer.Addresses))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // Served by the admin customer list, which filters on status and pages by email.
        builder.HasIndex(customer => customer.Role).HasDatabaseName("IX_Customers_Role");
    }
}
