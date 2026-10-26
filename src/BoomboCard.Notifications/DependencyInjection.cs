using BoomboCard.Notifications.Application;
using BoomboCard.Notifications.Application.Dispatch;
using BoomboCard.Notifications.Application.IntegrationEvents;
using BoomboCard.Notifications.Data;
using BoomboCard.Notifications.Data.Repositories;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoomboCard.Notifications;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Notifications module.
    ///
    /// <para>
    /// <b>The host must also register an <see cref="Domain.Sending.INotificationSender"/>.</b> It
    /// is deliberately not registered here, for the same reason <c>IPaymentGateway</c> and
    /// <c>IAccessTokenIssuer</c> are not: the provider, its credential, its base address and its
    /// timeout are deployment concerns. The practical consequence is that this module references
    /// no HTTP or SMTP package at all.
    /// </para>
    ///
    /// <para>
    /// The container is built with <c>ValidateOnBuild</c>, so forgetting the sender fails at
    /// startup rather than at the first email — which, given that the failure Onyx shipped was
    /// "no customer ever received anything and nothing recorded it", is the single most important
    /// thing for this module to fail loudly about.
    /// </para>
    /// </summary>
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext,
        IConfiguration? configuration = null)
    {
        var section = configuration?.GetSection("Notifications");

        var options = section?.Get<NotificationsOptions>() ?? new NotificationsOptions();

        services.AddSingleton(options);

        services.AddSingleton(section?.GetSection("Outbox").Get<OutboxOptions>()
            ?? new OutboxOptions());

        services.AddDbContext<NotificationsDbContext>((provider, dbOptions) =>
        {
            configureDbContext(dbOptions);
            dbOptions.UseBoomboCardConventions();
            dbOptions.AddInterceptors(provider.GetRequiredService<VersioningInterceptor>());
        });

        services.AddModulePersistence<NotificationsDbContext>();

        services.AddTransient(
            typeof(MediatR.IPipelineBehavior<,>),
            typeof(NotificationsUnitOfWorkBehavior<,>));

        services.AddScoped<INotificationRepository, NotificationRepository>();

        // Singleton: it holds one options object and formats two strings.
        services.AddSingleton<NotificationLinkBuilder>();

        services.AddSingleton<JobRunner<NotificationsDbContext>>();
        services.AddHostedService<OutboxDispatcher<NotificationsDbContext>>();

        // The job that sends. Onyx had no equivalent — its delivery mechanism was
        // `_ = Task.Run(...)` against a method that threw.
        services.AddHostedService<NotificationDispatchJob>();

        // The stale-claim sweep and the rendered-secret retention sweep.
        services.AddHostedService<NotificationMaintenanceJob>();

        // Reports, never corrects.
        services.AddHostedService<NotificationsIntegrityCheckJob>();

        return services;
    }

    /// <summary>Applies the module's migrations.</summary>
    public static async Task MigrateNotificationsAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>The assembly MediatR must scan to find this module's handlers.</summary>
    public static System.Reflection.Assembly NotificationsAssembly =>
        typeof(DependencyInjection).Assembly;
}
