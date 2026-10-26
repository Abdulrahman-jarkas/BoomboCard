using BoomboCard.Payments.Domain.PaymentAggregate;
using BoomboCard.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Payments.Data.Configuration;

/// <summary>
/// Money mapping, shared by every monetary figure in this module.
///
/// <para>
/// Owned types rather than value converters, which is the rule Catalog settled the hard way
/// (BUGS-AND-LESSONS §3): a converted value cannot be matched or ordered server-side, so the
/// query silently becomes a client-side scan. Explicit precision because Onyx <b>X10</b> claimed
/// "all monetary values use <c>decimal(18,2)</c>" while configuring precision nowhere.
/// </para>
/// </summary>
internal static class MoneyMapping
{
    public static void Map<TOwner>(OwnedNavigationBuilder<TOwner, Money> builder, string prefix)
        where TOwner : class
    {
        builder.Property(money => money.Amount)
            .HasColumnName($"{prefix}Amount")
            .HasPrecision(18, 2)
            .IsRequired();

        builder.Property(money => money.CurrencyCode)
            .HasColumnName($"{prefix}Currency")
            .HasMaxLength(3)
            .IsRequired();
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");

        builder.HasKey(payment => payment.Id);

        builder.Property(payment => payment.OrderId).IsRequired();
        builder.Property(payment => payment.CustomerId).IsRequired();
        builder.Property(payment => payment.CheckoutSessionId).IsRequired();
        builder.Property(payment => payment.InitiatedAtUtc).IsRequired();
        builder.Property(payment => payment.AuthorizedAtUtc);
        builder.Property(payment => payment.CapturedAtUtc);
        builder.Property(payment => payment.SettledOrFailedAtUtc);
        builder.Property(payment => payment.IndeterminateSinceUtc);
        builder.Property(payment => payment.SubmissionAttempts).IsRequired();
        builder.Property(payment => payment.DeclineCode).HasMaxLength(100);

        // Owned, not converted: support looks a payment up by this reference, so it has to be a
        // real column the provider can match server-side.
        builder.OwnsOne(payment => payment.Reference, reference =>
        {
            reference.Property(value => value.Value)
                .HasColumnName("Reference")
                .HasMaxLength(32)
                .IsRequired();

            reference.HasIndex(value => value.Value)
                .IsUnique()
                .HasDatabaseName("UX_Payments_Reference");
        });
        builder.Navigation(payment => payment.Reference).IsRequired();

        builder.OwnsOne(payment => payment.IdempotencyKey, key =>
        {
            key.Property(value => value.Value)
                .HasColumnName("IdempotencyKey")
                .HasMaxLength(IdempotencyKey.Length)
                .IsRequired();

            // Unique, because the key's entire purpose is to be the one thing that identifies
            // this payment to the provider across every attempt. Two payments sharing a key
            // would make the provider deduplicate one against the other — turning the mechanism
            // that prevents a double charge into one that silently loses a legitimate second
            // sale.
            key.HasIndex(value => value.Value)
                .IsUnique()
                .HasDatabaseName("UX_Payments_IdempotencyKey");
        });
        builder.Navigation(payment => payment.IdempotencyKey).IsRequired();

        // Nullable, and the whole of M4 is that it stays that way. An owned navigation rather
        // than a plain string so reconciliation can match it server-side against a provider
        // statement.
        builder.OwnsOne(payment => payment.ProviderReference, reference =>
        {
            reference.Property(value => value.Value)
                .HasColumnName("ProviderReference")
                .HasMaxLength(Domain.PaymentAggregate.ProviderReference.MaxLength);

            reference.HasIndex(value => value.Value)
                .HasDatabaseName("IX_Payments_ProviderReference");
        });

        builder.OwnsOne(payment => payment.Status, status =>
        {
            // A constrained, persisted status — Onyx A39's free-text "Pending"/"Paid" string.
            // Stored as the name rather than the number so the table is readable and a renumber
            // of the enum cannot silently reinterpret existing rows.
            status.Property(value => value.Value)
                .HasColumnName("Status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
        });
        builder.Navigation(payment => payment.Status).IsRequired();

        builder.OwnsOne(payment => payment.Amount, money => MoneyMapping.Map(money, "Amount"));
        builder.OwnsOne(payment => payment.RefundedAmount, money => MoneyMapping.Map(money, "Refunded"));
        builder.Navigation(payment => payment.Amount).IsRequired();
        builder.Navigation(payment => payment.RefundedAmount).IsRequired();

        builder.HasMany(payment => payment.Attempts)
            .WithOne()
            .HasForeignKey(attempt => attempt.PaymentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(payment => payment.Refunds)
            .WithOne()
            .HasForeignKey(refund => refund.PaymentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Payment.Attempts))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(Payment.Refunds))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // "Every payment for this order, newest first" — the support and admin query.
        //
        // NOT unique on OrderId, deliberately: a declined card must be followed by a second
        // attempt with a different one, so an order legitimately accumulates several payments.
        // The constraint that matters — at most one payment able to take money — is a predicate
        // over the STATUS, so it lives in PaymentClaim, whose primary key is the order id. See
        // that type for why a filtered unique index was refused.
        builder.HasIndex(payment => new { payment.OrderId, payment.InitiatedAtUtc })
            .HasDatabaseName("IX_Payments_Order");

        builder.HasIndex(payment => payment.CustomerId)
            .HasDatabaseName("IX_Payments_CustomerId");

        // The reconciliation job's query: payments whose real outcome is unknown, oldest first.
        builder.HasIndex(payment => payment.IndeterminateSinceUtc)
            .HasDatabaseName("IX_Payments_Indeterminate");
    }
}

internal sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> builder)
    {
        builder.ToTable("PaymentAttempts");

        builder.HasKey(attempt => attempt.Id);

        builder.Property(attempt => attempt.PaymentId).IsRequired();
        builder.Property(attempt => attempt.AttemptNumber).IsRequired();
        builder.Property(attempt => attempt.LatencyMs).IsRequired();
        builder.Property(attempt => attempt.OccurredAtUtc).IsRequired();

        builder.Property(attempt => attempt.Operation)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(attempt => attempt.Outcome)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(attempt => attempt.IdempotencyKey)
            .HasMaxLength(IdempotencyKey.Length)
            .IsRequired();

        builder.Property(attempt => attempt.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(attempt => attempt.ProviderReference)
            .HasMaxLength(Domain.PaymentAggregate.ProviderReference.MaxLength);
        builder.Property(attempt => attempt.ProviderCode).HasMaxLength(100);

        // Explicitly longer than the model-wide string convention, which is 500. A provider
        // message plus an exception type runs past that, and silent truncation of the only record
        // of why a payment failed is exactly the wrong trade — BUGS-AND-LESSONS §9, where the
        // outbox payload column was two fields from breaking every integration event.
        builder.Property(attempt => attempt.Detail)
            .HasMaxLength(PaymentAttempt.MaxDetailLength);

        // "Show me this payment's history, in order" — the support query A17 exists for.
        builder.HasIndex(attempt => new { attempt.PaymentId, attempt.OccurredAtUtc })
            .HasDatabaseName("IX_PaymentAttempts_History");

        // "What happened to the request carrying this key?" — the reconciliation join.
        builder.HasIndex(attempt => attempt.IdempotencyKey)
            .HasDatabaseName("IX_PaymentAttempts_IdempotencyKey");

        builder.HasIndex(attempt => attempt.CorrelationId)
            .HasDatabaseName("IX_PaymentAttempts_CorrelationId");
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("Refunds");

        builder.HasKey(refund => refund.Id);

        builder.Property(refund => refund.PaymentId).IsRequired();
        builder.Property(refund => refund.OccurredAtUtc).IsRequired();
        builder.Property(refund => refund.Reason).HasMaxLength(200).IsRequired();

        builder.Property(refund => refund.ProviderReference)
            .HasMaxLength(Domain.PaymentAggregate.ProviderReference.MaxLength)
            .IsRequired();

        builder.Property(refund => refund.IdempotencyKey).HasMaxLength(100).IsRequired();

        builder.OwnsOne(refund => refund.Amount, money => MoneyMapping.Map(money, "Amount"));
        builder.Navigation(refund => refund.Amount).IsRequired();

        // THE refund idempotency guarantee. A redelivered refund webhook, a retried admin
        // request, or a double-clicked button all refund once.
        //
        // Scoped to the payment rather than global, unlike UX_OrderPayments_Reference in Sales:
        // a refund key is chosen by the caller, so two unrelated payments could legitimately be
        // handed the same one, and failing that would be a false positive on somebody's money.
        builder.HasIndex(refund => new { refund.PaymentId, refund.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_Refunds_Idempotency");

        builder.HasIndex(refund => refund.ProviderReference)
            .HasDatabaseName("IX_Refunds_ProviderReference");
    }
}

internal sealed class ProviderWebhookConfiguration : IEntityTypeConfiguration<ProviderWebhook>
{
    public void Configure(EntityTypeBuilder<ProviderWebhook> builder)
    {
        builder.ToTable("ProviderWebhooks");

        builder.HasKey(webhook => webhook.Id);

        builder.Property(webhook => webhook.ProviderEventId).HasMaxLength(200).IsRequired();
        builder.Property(webhook => webhook.ReceivedAtUtc).IsRequired();
        builder.Property(webhook => webhook.SignatureVerified).IsRequired();
        builder.Property(webhook => webhook.Applied).IsRequired();
        builder.Property(webhook => webhook.PaymentId);
        builder.Property(webhook => webhook.RejectionReason).HasMaxLength(200);

        builder.Property(webhook => webhook.Kind)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(webhook => webhook.ProviderReference)
            .HasMaxLength(Domain.PaymentAggregate.ProviderReference.MaxLength)
            .IsRequired();

        builder.Property(webhook => webhook.Payload)
            .HasMaxLength(ProviderWebhook.MaxPayloadLength)
            .IsRequired();

        // Deduplication on the provider's own event id. Providers retry aggressively and deliver
        // out of order, so a "captured" notification arriving three times must capture once.
        builder.HasIndex(webhook => webhook.ProviderEventId)
            .IsUnique()
            .HasDatabaseName("UX_ProviderWebhooks_EventId");

        builder.HasIndex(webhook => webhook.ReceivedAtUtc)
            .HasDatabaseName("IX_ProviderWebhooks_ReceivedAt");
    }
}

/// <summary>
/// The conditional-uniqueness constraint, as a table. See <see cref="PaymentClaim"/> for the
/// reasoning; the configuration itself is three lines because the primary key does all the work.
/// </summary>
internal sealed class PaymentClaimConfiguration : IEntityTypeConfiguration<PaymentClaim>
{
    public void Configure(EntityTypeBuilder<PaymentClaim> builder)
    {
        builder.ToTable("PaymentClaims");

        // The natural key IS the constraint. A surrogate id here would mean the thing being
        // enforced was no longer the key, which is the entire point of this table.
        builder.HasKey(claim => claim.OrderId);

        builder.Property(claim => claim.PaymentId).IsRequired();
        builder.Property(claim => claim.ClaimedAtUtc).IsRequired();

        builder.HasIndex(claim => claim.PaymentId)
            .IsUnique()
            .HasDatabaseName("UX_PaymentClaims_PaymentId");
    }
}
