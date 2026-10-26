using BoomboCard.Promotions.Application;
using BoomboCard.Promotions.Application.Maintenance;
using BoomboCard.Promotions.Data;
using BoomboCard.Promotions.Data.Repositories;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoomboCard.Promotions;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Promotions module.
    ///
    /// <paramref name="configureDbContext"/> is supplied by the host, so the module carries no
    /// provider dependency and tests can point it at anything.
    /// </summary>
    public static IServiceCollection AddPromotionsModule(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext,
        IConfiguration? configuration = null)
    {
        var section = configuration?.GetSection("Promotions");

        services.AddSingleton(section?.Get<PromotionsOptions>() ?? new PromotionsOptions());

        services.AddSingleton(section?.GetSection("Outbox").Get<OutboxOptions>()
            ?? new OutboxOptions());

        services.AddDbContext<PromotionsDbContext>((provider, options) =>
        {
            configureDbContext(options);

            // Shared EF conventions — currently: a capped query with no ORDER BY throws rather
            // than logging a warning nobody reads. See ModuleDbContextOptions.
            options.UseBoomboCardConventions();

            options.AddInterceptors(provider.GetRequiredService<VersioningInterceptor>());
        });

        services.AddModulePersistence<PromotionsDbContext>();

        // Promotions' transaction boundary. Registered by the MODULE, not the host: the unit of
        // work is per module, so the behaviour that opens a transaction over it has to be too.
        services.AddTransient(
            typeof(MediatR.IPipelineBehavior<,>),
            typeof(PromotionsUnitOfWorkBehavior<,>));

        services.AddScoped<IPromotionRepository, PromotionRepository>();

        services.AddSingleton<JobRunner<PromotionsDbContext>>();
        services.AddHostedService<OutboxDispatcher<PromotionsDbContext>>();
        services.AddHostedService<PromotionExpiryJob>();

        return services;
    }

    /// <summary>
    /// Applies the module's migrations. Exposed as a capability so the host never has to name
    /// <c>PromotionsDbContext</c> — which is how the context stays internal.
    /// </summary>
    public static async Task MigratePromotionsAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PromotionsDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>The assembly MediatR must scan to find this module's handlers.</summary>
    public static System.Reflection.Assembly PromotionsAssembly => typeof(DependencyInjection).Assembly;
}
