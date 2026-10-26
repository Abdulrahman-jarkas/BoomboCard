using BoomboCard.Customers.Application;
using BoomboCard.Customers.Application.Auth;
using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Data;
using BoomboCard.Customers.Data.Repositories;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Jobs;
using BoomboCard.SharedKernel.Persistence.Outbox;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoomboCard.Customers;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Customers module.
    ///
    /// <paramref name="configureDbContext"/> is supplied by the host, so the module carries no
    /// provider dependency and tests can point it at anything.
    ///
    /// <para>
    /// The host must also register an <see cref="IAccessTokenIssuer"/>. It is not registered
    /// here and has no default, because the signing key and the token format are the host's
    /// concern — see that interface. A missing registration fails when the sign-in handler is
    /// resolved, which <c>ModuleWiringTests</c>' container-build assertion catches at startup
    /// rather than at the first login.
    /// </para>
    /// </summary>
    public static IServiceCollection AddCustomersModule(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext,
        IConfiguration? configuration = null)
    {
        var options = configuration?.GetSection("Customers").Get<CustomersOptions>()
            ?? new CustomersOptions();

        // At startup, not at first use. Onyx read _configuration["Jwt:Key"]! inside its login
        // action with a null-forgiving operator, so a missing key was a 500 on the first
        // authentication attempt in production rather than a refusal to start (S5, S15).
        options.Validate();

        services.AddSingleton(options);

        services.AddSingleton(configuration?.GetSection("Customers:Outbox").Get<OutboxOptions>()
            ?? new OutboxOptions());

        services.AddDbContext<CustomersDbContext>((provider, dbOptions) =>
        {
            configureDbContext(dbOptions);

            // Shared EF conventions — currently: a capped query with no ORDER BY throws rather
            // than logging a warning nobody reads. See ModuleDbContextOptions.
            dbOptions.UseBoomboCardConventions();

            dbOptions.AddInterceptors(provider.GetRequiredService<VersioningInterceptor>());
        });

        services.AddModulePersistence<CustomersDbContext>();

        // Customers' transaction boundary. Registered by the MODULE, not the host: the unit of
        // work is per module, so the behaviour that opens a transaction over it has to be too.
        // The constraint on ICustomersRequest is what keeps another module's commands out of
        // this transaction.
        services.AddTransient(
            typeof(MediatR.IPipelineBehavior<,>),
            typeof(CustomersUnitOfWorkBehavior<,>));

        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ICredentialTokenIssuer, CredentialTokenIssuer>();

        // Identity's hasher and nothing else from Identity. Singleton: it holds no state beyond
        // its options and is pure CPU work.
        services.AddSingleton<IPasswordHasher<PasswordOwner>, PasswordHasher<PasswordOwner>>();
        services.AddSingleton<IPasswordVerifier, IdentityPasswordVerifier>();
        services.AddSingleton<ClientFingerprint>();

        services.AddSingleton<JobRunner<CustomersDbContext>>();
        services.AddHostedService<OutboxDispatcher<CustomersDbContext>>();
        services.AddHostedService<Application.Maintenance.CustomersMaintenanceJob>();

        return services;
    }

    /// <summary>
    /// Applies the module's migrations.
    ///
    /// Exposed as a capability so the host never has to name <c>CustomersDbContext</c> — which is
    /// how the context stays internal, and therefore how the module boundary stays real rather
    /// than conventional.
    /// </summary>
    public static async Task MigrateCustomersAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>The assembly MediatR must scan to find this module's handlers.</summary>
    public static System.Reflection.Assembly CustomersAssembly => typeof(DependencyInjection).Assembly;
}
