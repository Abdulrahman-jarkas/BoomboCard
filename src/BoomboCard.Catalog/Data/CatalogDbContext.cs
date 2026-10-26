using System.Reflection;
using BoomboCard.Catalog.Domain.Exports;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Inbox;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Data;

/// <summary>
/// Catalog's own context, with its own schema.
///
/// Note what is NOT here: no IPublisher, no IDomainEventDispatcher, no SaveChangesAsync
/// override. All three reference projects put event dispatch inside the DbContext, which makes
/// a persistence call fan out into handlers, cross-module queries and HTTP. Here the DbContext
/// only persists; UnitOfWorkBehavior owns the transaction and DomainEventDispatcher owns
/// dispatch.
/// </summary>
internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public const string Schema = "catalog";

    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    internal DbSet<Product> Products => Set<Product>();

    internal DbSet<StockMovement> StockMovements => Set<StockMovement>();

    internal DbSet<ProductExport> ProductExports => Set<ProductExport>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    internal DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Schema per module, so each module's tables are visibly its own and extracting one
        // later is a smaller job. SQLite has no concept of schemas, so applying it there
        // produces a validation warning per entity and changes nothing — compared by provider
        // name rather than with Database.IsSqlite() so the module needs no provider package.
        if (Database.ProviderName is not SqliteProviderName)
        {
            modelBuilder.HasDefaultSchema(Schema);
        }

        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new JobRunConfiguration());

        // Must run after the configurations, since it walks the finished model.
        modelBuilder.UseClientGeneratedKeys();

        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Onyx D14: no precision was configured on any money column anywhere. SQLite tolerated
        // it; the PR claimed moving to PostgreSQL was "a one-line change in Program.cs", where
        // it would silently have changed rounding behaviour.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);

        configurationBuilder.Properties<string>().HaveMaxLength(500);

        // SQLite cannot order or range-compare DateTimeOffset, so store it as UTC ticks there.
        // A provider capability gap, handled at the persistence edge — the domain keeps using
        // DateTimeOffset and knows nothing about this.
        if (Database.ProviderName is SqliteProviderName)
        {
            configurationBuilder.Properties<DateTimeOffset>()
                .HaveConversion<DateTimeOffsetToUtcTicksConverter>();

            configurationBuilder.Properties<DateTimeOffset?>()
                .HaveConversion<NullableDateTimeOffsetToUtcTicksConverter>();
        }
    }
}
