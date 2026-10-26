using BoomboCard.Notifications.Domain.NotificationAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Notifications.Data.Configuration;

internal sealed class NotificationRequestConfiguration
    : IEntityTypeConfiguration<NotificationRequest>
{
    public void Configure(EntityTypeBuilder<NotificationRequest> builder)
    {
        builder.ToTable("Notifications");

        builder.HasKey(notification => notification.Id);

        builder.Property(notification => notification.Reference)
            .HasMaxLength(NotificationRequest.MaxReferenceLength)
            .IsRequired();

        builder.Property(notification => notification.CustomerId);
        builder.Property(notification => notification.QueuedAtUtc).IsRequired();
        builder.Property(notification => notification.ClaimedAtUtc);
        builder.Property(notification => notification.SentAtUtc);
        builder.Property(notification => notification.FinishedAtUtc);
        builder.Property(notification => notification.NextAttemptAtUtc);
        builder.Property(notification => notification.AttemptCount).IsRequired();
        builder.Property(notification => notification.ContainsSecret).IsRequired();
        builder.Property(notification => notification.Outcome).HasMaxLength(500);

        // Stored as names rather than numbers, so the table is readable and a renumbered enum
        // cannot silently reinterpret existing rows. Onyx A39's free-text status is the opposite
        // mistake; this is the constrained version of the same readability.
        builder.Property(notification => notification.Kind)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(notification => notification.Channel)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        // Explicitly longer than the model-wide 500-character string convention. BUGS-AND-LESSONS
        // §9 is exactly this: the outbox payload column inherited that convention and was two
        // fields away from silently failing every integration event. A truncated email body is
        // a half-finished message sent to a customer, so the aggregate refuses an over-long
        // render rather than letting the database decide.
        builder.Property(notification => notification.Subject)
            .HasMaxLength(NotificationRequest.MaxSubjectLength)
            .IsRequired();

        builder.Property(notification => notification.Body)
            .HasMaxLength(NotificationRequest.MaxBodyLength)
            .IsRequired();

        builder.OwnsOne(notification => notification.Recipient, recipient =>
        {
            recipient.Property(value => value.Value)
                .HasColumnName("Recipient")
                .HasMaxLength(NotificationAddress.MaxLength)
                .IsRequired();

            recipient.Property(value => value.DisplayName)
                .HasColumnName("RecipientName")
                .HasMaxLength(200);

            // "What have we sent to this address, and did any of it arrive?" — the first question
            // a support conversation asks.
            recipient.HasIndex(value => value.Value)
                .HasDatabaseName("IX_Notifications_Recipient");
        });
        builder.Navigation(notification => notification.Recipient).IsRequired();

        builder.OwnsOne(notification => notification.Status, status =>
        {
            status.Property(value => value.Value)
                .HasColumnName("Status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
        });
        builder.Navigation(notification => notification.Status).IsRequired();

        builder.HasMany(notification => notification.Attempts)
            .WithOne()
            .HasForeignKey(attempt => attempt.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(NotificationRequest.Attempts))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // THE GUARANTEE THAT A CUSTOMER GETS ONE EMAIL.
        //
        // One notification per (Kind, Reference), ever. The inbox claim already deduplicates a
        // redelivered integration event, and this index covers what the inbox cannot: two
        // DIFFERENT events that both want to tell the customer the same thing, a replayed outbox
        // after a restore, and a consumer registered twice by a wiring mistake.
        //
        // Onyx A8 had nothing here at all — no retry, no dead-letter, no delivery record — so a
        // duplicate send was not even a concept that could be discussed.
        builder.HasIndex(notification => new { notification.Kind, notification.Reference })
            .IsUnique()
            .HasDatabaseName("UX_Notifications_Idempotency");

        // The dispatcher's query: what is due, oldest first. The same shape as the outbox's
        // IX_OutboxMessages_Dispatch, because it is the same question.
        builder.HasIndex(notification => notification.NextAttemptAtUtc)
            .HasDatabaseName("IX_Notifications_Due");

        // The stale-claim sweep's query, and the health view's.
        builder.HasIndex(notification => notification.ClaimedAtUtc)
            .HasDatabaseName("IX_Notifications_Claimed");

        builder.HasIndex(notification => notification.CustomerId)
            .HasDatabaseName("IX_Notifications_CustomerId");
    }
}

internal sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> builder)
    {
        builder.ToTable("DeliveryAttempts");

        builder.HasKey(attempt => attempt.Id);

        builder.Property(attempt => attempt.NotificationId).IsRequired();
        builder.Property(attempt => attempt.AttemptNumber).IsRequired();
        builder.Property(attempt => attempt.LatencyMs).IsRequired();
        builder.Property(attempt => attempt.OccurredAtUtc).IsRequired();

        builder.Property(attempt => attempt.Outcome)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(attempt => attempt.Sender).HasMaxLength(64).IsRequired();
        builder.Property(attempt => attempt.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(attempt => attempt.ProviderMessageId).HasMaxLength(200);

        builder.Property(attempt => attempt.Detail)
            .HasMaxLength(DeliveryAttempt.MaxDetailLength);

        // "Show me this notification's history, in order" — the delivery record A8 says did not
        // exist.
        builder.HasIndex(attempt => new { attempt.NotificationId, attempt.OccurredAtUtc })
            .HasDatabaseName("IX_DeliveryAttempts_History");

        builder.HasIndex(attempt => attempt.CorrelationId)
            .HasDatabaseName("IX_DeliveryAttempts_CorrelationId");

        // "Which sender actually handled these?" — the query that catches a development default
        // reaching production.
        builder.HasIndex(attempt => new { attempt.Sender, attempt.OccurredAtUtc })
            .HasDatabaseName("IX_DeliveryAttempts_Sender");
    }
}
