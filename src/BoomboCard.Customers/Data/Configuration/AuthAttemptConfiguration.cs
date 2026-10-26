using BoomboCard.Customers.Domain.AuthAttempts;
using BoomboCard.Customers.Domain.CustomerAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Customers.Data.Configuration;

internal sealed class AuthAttemptConfiguration : IEntityTypeConfiguration<AuthAttempt>
{
    public void Configure(EntityTypeBuilder<AuthAttempt> builder)
    {
        builder.ToTable("AuthAttempts");

        builder.HasKey(attempt => attempt.Id);

        // Nullable, and with no foreign key: the rows for an address that matched no account are
        // the most interesting ones in the table, and a required FK would make them impossible
        // to store. Onyx's login simply returned Unauthorized() and recorded nothing at all,
        // which is why a guessing campaign against it would have left no trace (S12).
        builder.Property(attempt => attempt.CustomerId);

        builder.Property(attempt => attempt.Email)
            .HasMaxLength(EmailAddress.MaxLength)
            .IsRequired();

        builder.Property(attempt => attempt.Outcome)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // 32 hex characters: HMAC-SHA256 truncated to 16 bytes. See ClientFingerprint.
        builder.Property(attempt => attempt.ClientFingerprint).HasMaxLength(32);

        builder.Property(attempt => attempt.OccurredAtUtc).IsRequired();

        // Two queries, two indexes.
        //
        // "Recent attempts for this account", newest first — what an administrator looks at when
        // a customer reports a problem, and what the account-activity view will read.
        builder.HasIndex(attempt => new { attempt.CustomerId, attempt.OccurredAtUtc })
            .HasDatabaseName("IX_AuthAttempts_Customer");

        // "Recent attempts from this client", which is the one that identifies credential
        // stuffing: the same fingerprint failing against many different addresses.
        builder.HasIndex(attempt => new { attempt.ClientFingerprint, attempt.OccurredAtUtc })
            .HasDatabaseName("IX_AuthAttempts_Client");

        // The retention sweep deletes by age, so it gets its own key rather than scanning.
        // Onyx D11 had no indexes anywhere at all.
        builder.HasIndex(attempt => attempt.OccurredAtUtc)
            .HasDatabaseName("IX_AuthAttempts_OccurredAt");
    }
}
