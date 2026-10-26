using BoomboCard.Sales.Application;
using BoomboCard.Sales.Application.Reconciliation;
using BoomboCard.Sales.Data;
using BoomboCard.Sales.Data.Repositories;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoomboCard.Sales;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Sales module.
    ///
    /// <paramref name="configureDbContext"/> is supplied by the host, so the module carries no
    /// provider dependency and tests can point it at anything.
    /// </summary>
    public static IServiceCollection AddSalesModule(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext,
        IConfiguration? configuration = null)
    {
        var section = configuration?.GetSection("Sales");

        services.AddSingleton(section?.Get<SalesOptions>() ?? new SalesOptions());

        services.AddSingleton(section?.GetSection("Outbox").Get<OutboxOptions>()
            ?? new OutboxOptions());

        services.AddDbContext<SalesDbContext>((provider, options) =>
        {
            configureDbContext(options);
            options.UseBoomboCardConventions();
            options.AddInterceptors(provider.GetRequiredService<VersioningInterceptor>());
        });

        // Registers IUnitOfWork<SalesDbContext>, IDomainEventDispatcher<SalesDbContext>,
        // IOutboxWriter<SalesDbContext> and IInboxGuard<SalesDbContext> — all typed, nothing
        // against the bare interfaces. See BUGS-AND-LESSONS §7 for what the untyped version
        // silently did.
        services.AddModulePersistence<SalesDbContext>();

        services.AddTransient(
            typeof(MediatR.IPipelineBehavior<,>),
            typeof(SalesUnitOfWorkBehavior<,>));

        services.AddScoped<ISalesRepository, SalesRepository>();

        services.AddSingleton<JobRunner<SalesDbContext>>();
        services.AddHostedService<OutboxDispatcher<SalesDbContext>>();

        // Recovery layer 2 of CONCEPTS.md §13, and the layer that IS the guarantee:
        // nothing else releases a session that simply stopped, because a customer closing
        // the tab sends no request.
        services.AddHostedService<CheckoutExpiryJob>();

        // Recovery layer 3 — the Catalog/Sales handshake. Finds bugs, not failures, and
        // should find nothing.
        services.AddHostedService<OrphanSaleReconciliationJob>();

        // Not a recovery layer: the audit that proves the totals. Reports, never corrects.
        services.AddHostedService<SalesIntegrityCheckJob>();

        return services;
    }

    /// <summary>Applies the module's migrations.</summary>
    public static async Task MigrateSalesAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>The assembly MediatR must scan to find this module's handlers.</summary>
    public static System.Reflection.Assembly SalesAssembly => typeof(DependencyInjection).Assembly;
}
