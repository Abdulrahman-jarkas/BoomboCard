using System.Reflection;
using BoomboCard.Notifications.Domain.NotificationAggregate;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Inbox;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Notifications.Data;

/// <summary>
/// Notifications' own context, with its own schema, outbox and inbox.
///
/// <para>
/// The inbox matters more here than in any other module: this one subscribes to <b>eleven</b>
/// integration events across four modules, and outbox delivery is at-least-once everywhere. The
/// inbox claim is the first guard against a redelivery; <c>UX_Notifications_Idempotency</c> on
/// <c>(Kind, Reference)</c> is the one that actually guarantees a customer gets one email.
/// </para>
/// </summary>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    public const string Schema = "notifications";

    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    internal DbSet<NotificationRequest> Notifications => Set<NotificationRequest>();

    internal DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    internal DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (Database.ProviderName is not SqliteProviderName)
        {
            modelBuilder.HasDefaultSchema(Schema);
        }

        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new JobRunConfiguration());

        modelBuilder.UseClientGeneratedKeys();

        base.OnModelCreating(modelBuilder);
    }
}
