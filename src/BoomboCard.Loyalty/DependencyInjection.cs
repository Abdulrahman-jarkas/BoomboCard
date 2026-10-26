using BoomboCard.Loyalty.Application;
using BoomboCard.Loyalty.Application.Maintenance;
using BoomboCard.Loyalty.Data;
using BoomboCard.Loyalty.Data.Repositories;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoomboCard.Loyalty;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Loyalty module.
    ///
    /// <paramref name="configureDbContext"/> is supplied by the host, so the module carries no
    /// provider dependency and tests can point it at anything.
    /// </summary>
    public static IServiceCollection AddLoyaltyModule(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext,
        IConfiguration? configuration = null)
    {
        var section = configuration?.GetSection("Loyalty");

        services.AddSingleton(section?.Get<LoyaltyOptions>() ?? new LoyaltyOptions());

        services.AddSingleton(section?.GetSection("Outbox").Get<OutboxOptions>()
            ?? new OutboxOptions());

        services.AddDbContext<LoyaltyDbContext>((provider, options) =>
        {
            configureDbContext(options);
            options.UseBoomboCardConventions();
            options.AddInterceptors(provider.GetRequiredService<VersioningInterceptor>());
        });

        services.AddModulePersistence<LoyaltyDbContext>();

        services.AddTransient(
            typeof(MediatR.IPipelineBehavior<,>),
            typeof(LoyaltyUnitOfWorkBehavior<,>));

        services.AddScoped<IPointsRepository, PointsRepository>();

        services.AddSingleton<JobRunner<LoyaltyDbContext>>();
        services.AddHostedService<OutboxDispatcher<LoyaltyDbContext>>();
        services.AddHostedService<PointsExpiryJob>();
        services.AddHostedService<PointsIntegrityCheckJob>();

        return services;
    }

    /// <summary>Applies the module's migrations.</summary>
    public static async Task MigrateLoyaltyAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LoyaltyDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>The assembly MediatR must scan to find this module's handlers.</summary>
    public static System.Reflection.Assembly LoyaltyAssembly => typeof(DependencyInjection).Assembly;
}
