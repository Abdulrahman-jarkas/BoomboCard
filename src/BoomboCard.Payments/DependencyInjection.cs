using BoomboCard.Payments.Application;
using BoomboCard.Payments.Application.Reconciliation;
using BoomboCard.Payments.Data;
using BoomboCard.Payments.Data.Repositories;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoomboCard.Payments;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Payments module.
    ///
    /// <para>
    /// <b>The host must also register an <see cref="Domain.Gateway.IPaymentGateway"/>.</b> It is
    /// deliberately not registered here, for the same reason <c>IAccessTokenIssuer</c> is not
    /// registered by Customers: the base address, the API key, the retry policy and the HTTP
    /// stack are all deployment concerns, and a module that decides what a payment <i>means</i>
    /// should not also own how to reach a particular vendor. The practical consequence is that
    /// this module references no HTTP package at all.
    /// </para>
    ///
    /// <para>
    /// The container is built with <c>ValidateOnBuild</c>, so forgetting the gateway fails at
    /// startup rather than at the first payment.
    /// </para>
    /// </summary>
    public static IServiceCollection AddPaymentsModule(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext,
        IConfiguration? configuration = null)
    {
        var section = configuration?.GetSection("Payments");

        services.AddSingleton(section?.Get<PaymentsOptions>() ?? new PaymentsOptions());

        services.AddSingleton(section?.GetSection("Outbox").Get<OutboxOptions>()
            ?? new OutboxOptions());

        services.AddDbContext<PaymentsDbContext>((provider, options) =>
        {
            configureDbContext(options);
            options.UseBoomboCardConventions();
            options.AddInterceptors(provider.GetRequiredService<VersioningInterceptor>());
        });

        services.AddModulePersistence<PaymentsDbContext>();

        services.AddTransient(
            typeof(MediatR.IPipelineBehavior<,>),
            typeof(PaymentsUnitOfWorkBehavior<,>));

        services.AddScoped<IPaymentRepository, PaymentRepository>();

        services.AddSingleton<JobRunner<PaymentsDbContext>>();
        services.AddHostedService<OutboxDispatcher<PaymentsDbContext>>();

        // The job that makes an unanswered charge request resolvable instead of a guess. It is
        // what IdempotencyKey, AttemptOutcome.Indeterminate and IPaymentGateway.EnquireAsync all
        // exist to make possible.
        services.AddHostedService<PaymentReconciliationJob>();

        // Reports, never corrects — unlike the reconciliation job above, which retrieves an
        // answer from the system of record rather than computing one.
        services.AddHostedService<PaymentsIntegrityCheckJob>();

        return services;
    }

    /// <summary>Applies the module's migrations.</summary>
    public static async Task MigratePaymentsAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>The assembly MediatR must scan to find this module's handlers.</summary>
    public static System.Reflection.Assembly PaymentsAssembly => typeof(DependencyInjection).Assembly;
}
