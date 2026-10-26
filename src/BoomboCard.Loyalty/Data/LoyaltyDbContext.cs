using System.Reflection;
using BoomboCard.Loyalty.Domain.Ledger;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Inbox;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Loyalty.Data;

/// <summary>
/// Loyalty's own context, with its own schema, outbox and inbox.
///
/// Its type is this module's DI scope key — handlers inject
/// <c>IUnitOfWork&lt;LoyaltyDbContext&gt;</c>, which is what keeps them from being handed
/// another module's unit of work.
/// </summary>
internal sealed class LoyaltyDbContext(DbContextOptions<LoyaltyDbContext> options)
    : DbContext(options)
{
    public const string Schema = "loyalty";

    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    internal DbSet<PointsAccount> Accounts => Set<PointsAccount>();

    internal DbSet<PointsLedgerEntry> Ledger => Set<PointsLedgerEntry>();

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
