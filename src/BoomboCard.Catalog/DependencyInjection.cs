using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoomboCard.Catalog;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Catalog module.
    ///
    /// <paramref name="configureDbContext"/> is supplied by the host, so the module carries no
    /// provider dependency and tests can point it at anything.
    /// </summary>
    public static IServiceCollection AddCatalogModule(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext,
        IConfiguration? configuration = null)
    {
        // Options bound from the "Catalog" section, with sane defaults when it is absent.
        // Onyx A15 bound PaymentGatewayOptions from configuration and then hardcoded the URL in
        // the client anyway, so the setting did nothing and the provider could not be changed.
        var section = configuration?.GetSection("Catalog");

        services.AddSingleton(section?.GetSection("Export").Get<Application.Exports.ExportOptions>()
            ?? new Application.Exports.ExportOptions());

        services.AddSingleton(section?.GetSection("Reconciliation").Get<Application.Reconciliation.ReconciliationOptions>()
            ?? new Application.Reconciliation.ReconciliationOptions());

        services.AddSingleton(section?.GetSection("Outbox").Get<OutboxOptions>()
            ?? new OutboxOptions());

        services.AddDbContext<CatalogDbContext>((provider, options) =>
        {
            configureDbContext(options);

            // Shared EF conventions — currently: a capped query with no ORDER BY throws instead
            // of logging a warning nobody reads. See ModuleDbContextOptions.
            options.UseBoomboCardConventions();

            // Bumps AggregateRoot.Version on every modified aggregate, which is what turns the
            // concurrency token into an actual guarantee.
            options.AddInterceptors(provider.GetRequiredService<VersioningInterceptor>());
        });

        services.AddModulePersistence<CatalogDbContext>();

        // Catalog's transaction boundary. Registered by the MODULE, not by the host: the unit
        // of work is per module, so the behaviour that opens a transaction over it has to be
        // too. The generic constraint on ICatalogRequest is what keeps another module's
        // commands out of this transaction — see CatalogUnitOfWorkBehavior.
        services.AddTransient(
            typeof(MediatR.IPipelineBehavior<,>),
            typeof(Application.CatalogUnitOfWorkBehavior<,>));

        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IProductExportRepository, ProductExportRepository>();

        // Background work. Every job records its attempt and outcome in JobRuns, and none of
        // them can take the host down: Onyx A21 ran two jobs in one process with no error
        // boundary, so either one faulting killed both with nothing to restart them.
        services.AddSingleton<JobRunner<CatalogDbContext>>();
        services.AddHostedService<OutboxDispatcher<CatalogDbContext>>();
        services.AddHostedService<Application.Exports.ProductExportGenerator>();
        services.AddHostedService<Application.Reconciliation.StockIntegrityCheckJob>();
        services.AddHostedService<Application.Reconciliation.ExportMaintenanceJob>();

        return services;
    }

    /// <summary>
    /// Applies the module's migrations.
    ///
    /// Exposed as a capability so the host never has to name CatalogDbContext — which is how the
    /// context stays internal, and therefore how the module boundary stays real rather than
    /// conventional. An architecture test asserts no persistence type is publicly visible.
    ///
    /// Migrations, never EnsureCreated: Onyx D12 called EnsureCreated() from two processes
    /// against one SQLite file, and because it does nothing when the database already exists,
    /// every schema change meant deleting the database and losing all data.
    /// </summary>
    public static async Task MigrateCatalogAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>The assembly MediatR must scan to find this module's handlers.</summary>
    public static System.Reflection.Assembly CatalogAssembly => typeof(DependencyInjection).Assembly;
}
