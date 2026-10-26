using System.Reflection;
using BoomboCard.Customers.Domain.AuthAttempts;
using BoomboCard.Customers.Domain.Credentials;
using BoomboCard.Customers.Domain.CustomerAggregate;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Inbox;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Customers.Data;

/// <summary>
/// Customers' own context, with its own schema, its own outbox and its own inbox.
///
/// Note what is NOT here: no IPublisher, no IDomainEventDispatcher, no SaveChangesAsync
/// override. The DbContext only persists; the module's unit-of-work behaviour owns the
/// transaction and the dispatcher owns dispatch.
///
/// Its type is also this module's DI scope key — handlers inject
/// <c>IUnitOfWork&lt;CustomersDbContext&gt;</c>, which is what keeps them from being handed
/// another module's unit of work. See <c>IUnitOfWork&lt;TScope&gt;</c>.
/// </summary>
internal sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options)
    : DbContext(options)
{
    public const string Schema = "customers";

    private const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

    internal DbSet<Customer> Customers => Set<Customer>();

    internal DbSet<Address> Addresses => Set<Address>();

    internal DbSet<AuthAttempt> AuthAttempts => Set<AuthAttempt>();

    internal DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    internal DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();

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
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<string>().HaveMaxLength(500);

        // SQLite cannot order or range-compare DateTimeOffset, so store it as UTC ticks there.
        // A provider capability gap handled at the persistence edge — the domain keeps using
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
