using BoomboCard.Customers.Domain.Credentials;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Customers.Data.Configuration;

/*
 * The two token tables. Identical in shape and deliberately separate — see
 * EmailVerificationToken for why sharing a table would invite sharing a lookup, and a lookup
 * that accepts either kind would let a verification link reset a password.
 *
 * Both are hex SHA-256, uniquely indexed, which makes redemption an indexed equality match on a
 * hash rather than a string comparison in application code. No timing side channel to reason
 * about, and nothing for a later edit to turn into `==`.
 */

internal sealed class PasswordResetTokenConfiguration
    : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("PasswordResetTokens");

        builder.HasKey(token => token.Id);

        // The concurrency token, and the thing that makes single use a guarantee rather than a
        // hope: two requests presenting the same token both pass the IsRedeemed check, and the
        // version predicate is what makes exactly one of them win. See PasswordResetToken.Redeem.
        builder.Property(token => token.Version)
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(token => token.CustomerId).IsRequired();

        builder.Property(token => token.TokenHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(token => token.IssuedAtUtc).IsRequired();
        builder.Property(token => token.ExpiresAtUtc).IsRequired();

        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("UX_PasswordResetTokens_Hash");

        // The sweep deletes spent and expired rows by age.
        builder.HasIndex(token => token.ExpiresAtUtc)
            .HasDatabaseName("IX_PasswordResetTokens_ExpiresAt");
    }
}

internal sealed class EmailVerificationTokenConfiguration
    : IEntityTypeConfiguration<EmailVerificationToken>
{
    public void Configure(EntityTypeBuilder<EmailVerificationToken> builder)
    {
        builder.ToTable("EmailVerificationTokens");

        builder.HasKey(token => token.Id);

        builder.Property(token => token.Version)
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(token => token.CustomerId).IsRequired();

        builder.Property(token => token.TokenHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(token => token.IssuedAtUtc).IsRequired();
        builder.Property(token => token.ExpiresAtUtc).IsRequired();

        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("UX_EmailVerificationTokens_Hash");

        builder.HasIndex(token => token.ExpiresAtUtc)
            .HasDatabaseName("IX_EmailVerificationTokens_ExpiresAt");
    }
}
