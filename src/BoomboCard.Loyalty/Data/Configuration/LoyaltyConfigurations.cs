using BoomboCard.Loyalty.Domain.Ledger;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Loyalty.Data.Configuration;

internal sealed class PointsAccountConfiguration : IEntityTypeConfiguration<PointsAccount>
{
    public void Configure(EntityTypeBuilder<PointsAccount> builder)
    {
        builder.ToTable("PointsAccounts");

        builder.HasKey(account => account.Id);

        // The optimistic concurrency token, and here it is what carries the "balance never goes
        // negative" invariant. Two simultaneous redemptions of the last 500 points both read
        // 500 and both pass the sufficiency check; the version predicate makes exactly one win,
        // the loser affects zero rows, and the retry reads 0 and returns InsufficientPoints.
        //
        // This is why the balance is a column rather than a SUM over the ledger: a SUM cannot
        // be the subject of a concurrency token. See PointsAccount.Balance.
        builder.Property(account => account.Version)
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(account => account.CustomerId).IsRequired();
        builder.Property(account => account.Balance).IsRequired();
        builder.Property(account => account.IsClosed).IsRequired();
        builder.Property(account => account.OpenedAtUtc).IsRequired();
        builder.Property(account => account.ClosedAtUtc);

        // One account per customer, at the database. The account is opened by a handler reacting
        // to CustomerRegisteredIntegrationEvent, and that delivery is at-least-once — so the
        // inbox claim is the first guard and this index is the backstop. Onyx D10 had no unique
        // constraint on any natural key anywhere.
        builder.HasIndex(account => account.CustomerId)
            .IsUnique()
            .HasDatabaseName("UX_PointsAccounts_CustomerId");
    }
}

internal sealed class PointsLedgerEntryConfiguration : IEntityTypeConfiguration<PointsLedgerEntry>
{
    /// <summary>Matches <c>StockMovement.Reference</c>, which holds the same kind of handle.</summary>
    public const int MaxReferenceLength = 200;

    public void Configure(EntityTypeBuilder<PointsLedgerEntry> builder)
    {
        builder.ToTable("PointsLedger");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.AccountId).IsRequired();
        builder.Property(entry => entry.CustomerId).IsRequired();
        builder.Property(entry => entry.Delta).IsRequired();

        builder.Property(entry => entry.Reason)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(entry => entry.Reference)
            .HasMaxLength(MaxReferenceLength)
            .IsRequired();

        builder.Property(entry => entry.BalanceAfter).IsRequired();
        builder.Property(entry => entry.OccurredAtUtc).IsRequired();

        builder.HasOne<PointsAccount>()
            .WithMany()
            .HasForeignKey(entry => entry.AccountId)
            // Restrict, not Cascade. The ledger is a financial record: deleting an account must
            // not be able to take its history with it, and an account is closed rather than
            // deleted anyway. Onyx D13 cascaded a hard DELETE from a customer into their orders
            // and payments, destroying exactly this kind of record.
            .OnDelete(DeleteBehavior.Restrict);

        // IDEMPOTENCY. One movement per account, reference and reason — the same three-column
        // shape Catalog uses for stock, and what makes at-least-once outbox delivery safe: a
        // redelivered "order paid" credits once, and a retried checkout debits once.
        //
        // Reason is in the key because one order legitimately produces an Earned entry and, if
        // refunded, a Reversed one against the same reference.
        builder.HasIndex(entry => new { entry.AccountId, entry.Reference, entry.Reason })
            .IsUnique()
            .HasDatabaseName("UX_PointsLedger_Idempotency");

        // The statement, newest first, keyset paged over the timestamp.
        builder.HasIndex(entry => new { entry.AccountId, entry.OccurredAtUtc })
            .HasDatabaseName("IX_PointsLedger_Statement");

        // The expiry job's two sums: credits before a cutoff, and everything consumed. Both
        // filter on account and reason and range on the timestamp.
        builder.HasIndex(entry => new { entry.AccountId, entry.Reason, entry.OccurredAtUtc })
            .HasDatabaseName("IX_PointsLedger_Expiry");

        // The release path is told a checkout session id and nothing else, so it looks rows up
        // by reference across accounts.
        builder.HasIndex(entry => entry.Reference)
            .HasDatabaseName("IX_PointsLedger_Reference");
    }
}
