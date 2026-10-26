using System.Reflection;
using BoomboCard.Payments.Domain.PaymentAggregate;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Inbox;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Payments.Data;

/// <summary>
/// Payments' own context, with its own schema, outbox and inbox.
/// </summary>
internal sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options)
    : DbContext(options)
{
    public const string Schema = "payments";

    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    internal DbSet<Payment> Payments => Set<Payment>();

    internal DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();

    internal DbSet<Refund> Refunds => Set<Refund>();

    internal DbSet<ProviderWebhook> ProviderWebhooks => Set<ProviderWebhook>();

    internal DbSet<PaymentClaim> PaymentClaims => Set<PaymentClaim>();

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
