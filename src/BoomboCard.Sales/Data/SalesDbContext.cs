using System.Reflection;
using BoomboCard.Sales.Domain.CheckoutSessionAggregate;
using BoomboCard.Sales.Domain.Numbering;
using BoomboCard.Sales.Domain.OrderAggregate;
using BoomboCard.Sales.Domain.Pricing;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Inbox;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Sales.Data;

/// <summary>
/// Sales' own context, with its own schema, outbox and inbox.
/// </summary>
internal sealed class SalesDbContext(DbContextOptions<SalesDbContext> options) : DbContext(options)
{
    public const string Schema = "sales";

    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    internal DbSet<CheckoutSession> CheckoutSessions => Set<CheckoutSession>();

    internal DbSet<CheckoutLine> CheckoutLines => Set<CheckoutLine>();

    internal DbSet<QuoteAdjustment> QuoteAdjustments => Set<QuoteAdjustment>();

    internal DbSet<Order> Orders => Set<Order>();

    internal DbSet<OrderItem> OrderItems => Set<OrderItem>();

    internal DbSet<OrderAdjustment> OrderAdjustments => Set<OrderAdjustment>();

    internal DbSet<OrderPayment> OrderPayments => Set<OrderPayment>();

    internal DbSet<OrderNumberSequence> OrderNumberSequences => Set<OrderNumberSequence>();

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

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<string>().HaveMaxLength(500);

        if (Database.ProviderName is SqliteProviderName)
        {
            configurationBuilder.Properties<DateTimeOffset>()
                .HaveConversion<DateTimeOffsetToUtcTicksConverter>();

            configurationBuilder.Properties<DateTimeOffset?>()
                .HaveConversion<NullableDateTimeOffsetToUtcTicksConverter>();
        }
    }
}
