using System.Reflection;
using BoomboCard.Api.Authentication;
using Microsoft.AspNetCore.Builder;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Inbox;
using BoomboCard.SharedKernel.Persistence.Outbox;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BoomboCard.ArchitectureTests;

/// <summary>
/// The composition root, asserted.
///
/// <para>
/// These exist because of a bug this codebase shipped in its own plumbing.
/// <c>AddModulePersistence&lt;TContext&gt;</c> registered <c>IUnitOfWork</c>,
/// <c>IDomainEventDispatcher</c> and <c>IOutboxWriter</c> unkeyed. With one module that is
/// correct. With two, the second call silently replaces the first for every handler in the
/// process: a Catalog handler is handed the Sales unit of work, saves the Sales context, writes
/// none of its own rows, and <c>ModuleUnitOfWorkBehavior</c> then commits a transaction over a
/// context with no changes. No exception. No log. No data. It is Onyx D3 — "multi-write use
/// cases are not transactional" — reached by a completely different road, and it would have
/// been introduced by the very next commit.
/// </para>
///
/// <para>
/// The fix was to type the registrations on the module's own context. The fix needs tests,
/// because the new failure mode is quieter than the old one: a command with no module marker
/// does not fail to compile, it just silently runs with no transaction. Hence
/// <see cref="EveryTransactionalRequest_CarriesExactlyOneModuleMarker"/>.
/// </para>
///
/// <para>
/// Program.cs has claimed since it was written that "there is a test asserting the container
/// builds". There was not. That is a BoomboCard X-number, and
/// <see cref="TheContainerBuilds_WithScopeValidationOn"/> is it.
/// </para>
/// </summary>
public sealed class ModuleWiringTests
{
    public static TheoryData<string> AllModules => Modules.Names();

    /// <summary>
    /// Never connected to. The provider is configured because EF needs one to build a model,
    /// and the container is only ever built and inspected here, never used to run a query.
    /// </summary>
    private const string UnusedConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=BoomboCardWiringTests;Trusted_Connection=True";

    /// <summary>
    /// The settings the host and the Customers module refuse to start without. Supplied here
    /// rather than defaulted away, because the refusal is itself the behaviour being preserved —
    /// see <c>JwtOptions.Validate</c> and <c>CustomersOptions.Validate</c>.
    /// </summary>
    private static readonly IConfiguration HostConfiguration =
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "architecture-tests-signing-key-at-least-32-bytes-long",
                ["Jwt:Issuer"] = "boombocard-tests",
                ["Jwt:Audience"] = "boombocard-tests"
            })
            .Build();

    // ----------------------------------------------------------------- request markers

    /// <summary>
    /// Exactly one. Zero means the command gets no transaction at all — the silent failure the
    /// typed registrations traded for the loud one. Two would mean two behaviours both open a
    /// transaction for it, on two different contexts, and the inner one would see
    /// <c>HasActiveTransaction</c> false on its own connection and open a second.
    /// </summary>
    [Fact]
    public void EveryTransactionalRequest_CarriesExactlyOneModuleMarker()
    {
        var markers = Modules.All.Select(module => module.RequestMarker).ToList();
        var offenders = new List<string>();

        foreach (var request in AllTransactionalRequests())
        {
            var carried = markers.Count(marker => marker.IsAssignableFrom(request));

            if (carried != 1)
            {
                offenders.Add($"{request.FullName} carries {carried} module markers");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A command's module marker is what gets it a transaction, so every "
            + "ITransactionalRequest must carry exactly one."
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// A marker nobody registered a behaviour for is worse than no marker: every command
    /// carrying it looks correctly declared and runs with no transaction.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void EveryModuleMarker_HasARegisteredTransactionBoundary(string moduleName)
    {
        var module = Modules.ByName(moduleName);
        var services = ComposeHost();

        var behaviours = services
            .Where(descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>))
            .Select(descriptor => descriptor.ImplementationType)
            .Where(implementation => implementation is not null && DerivesFromModuleBehaviour(implementation))
            .Where(implementation => ConstrainsRequestsTo(implementation!, module.RequestMarker))
            .ToList();

        Assert.True(
            behaviours.Count == 1,
            $"{module.Name} must register exactly one ModuleUnitOfWorkBehavior constrained to "
            + $"{module.RequestMarker.Name}; found {behaviours.Count}.");
    }

    // ----------------------------------------------------------------- per-module services

    /// <summary>
    /// The bare interfaces exist only so the behaviour has something to depend on. A
    /// registration against one of them is the bug: it resolves for every module, and the last
    /// one registered wins.
    /// </summary>
    [Fact]
    public void NothingIsRegisteredAgainstTheUntypedPerModuleServices()
    {
        var services = ComposeHost();

        Type[] untyped =
        [
            typeof(IUnitOfWork),
            typeof(IDomainEventDispatcher),
            typeof(IOutboxWriter),
            typeof(IInboxGuard)
        ];

        var offenders = services
            .Where(descriptor => untyped.Contains(descriptor.ServiceType))
            .Select(descriptor =>
                $"{descriptor.ServiceType.Name} -> {descriptor.ImplementationType?.Name ?? "factory"}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Per-module services must be registered against IUnitOfWork<TContext> and friends, "
            + "never the bare interface — an unkeyed registration resolves for every module and "
            + "the last one wins. Offenders: " + string.Join(", ", offenders));
    }

    [Theory]
    [MemberData(nameof(AllModules))]
    public void EveryModule_RegistersItsOwnScopedServices(string moduleName)
    {
        var module = Modules.ByName(moduleName);
        var services = ComposeHost();

        Type[] expected =
        [
            typeof(IUnitOfWork<>).MakeGenericType(module.DbContextType),
            typeof(IDomainEventDispatcher<>).MakeGenericType(module.DbContextType),
            typeof(IOutboxWriter<>).MakeGenericType(module.DbContextType),
            typeof(IInboxGuard<>).MakeGenericType(module.DbContextType)
        ];

        var missing = expected
            .Where(service => services.All(descriptor => descriptor.ServiceType != service))
            .Select(service => service.Name)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{module.Name} must call AddModulePersistence<{module.DbContextType.Name}>(). "
            + "Missing: " + string.Join(", ", missing));
    }

    // ----------------------------------------------------------------- the container itself

    /// <summary>
    /// Onyx A2 switched <c>ValidateScopes</c> and <c>ValidateOnBuild</c> OFF to silence the
    /// validator that had correctly detected a singleton cache holding a scoped
    /// <c>DbContext</c> (A1). Both stay on here, in every environment — which is only a
    /// guarantee if something actually builds the container with them on.
    /// </summary>
    [Fact]
    public void TheContainerBuilds_WithScopeValidationOn()
    {
        var services = ComposeHost();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISender>());

        // A singleton: if it ever captured a scoped DbContext — the captive dependency that is
        // Onyx A1 — resolving it from the root would be the thing that caught it.
        Assert.NotNull(provider.GetRequiredService<ICommandDispatcher>());
    }

    // ----------------------------------------------------------------- behaviour routing

    /// <summary>
    /// The end-to-end version of the marker rules: take a real command from the module, close
    /// the pipeline interface over it, and assert the module's transaction boundary is in the
    /// resolved pipeline.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void ACommand_ResolvesItsOwnModulesTransactionBoundary(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var command = AllTransactionalRequests()
            .FirstOrDefault(request => module.RequestMarker.IsAssignableFrom(request));

        Assert.NotNull(command);

        var behaviours = ResolveBehavioursFor(command!);

        var boundaries = behaviours
            .Where(behaviour => DerivesFromModuleBehaviour(behaviour!.GetType()))
            .ToList();

        Assert.True(
            boundaries.Count == 1,
            $"{command!.Name} must resolve exactly one transaction boundary; got "
            + $"{boundaries.Count}: "
            + string.Join(", ", boundaries.Select(behaviour => behaviour!.GetType().Name)));
    }

    /// <summary>
    /// Queries deliberately get none. dome-gym's middleware called
    /// <c>BeginTransactionAsync()</c> at the top of every request including pure reads, and held
    /// it open for the whole request lifetime.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void AQuery_ResolvesNoTransactionBoundary(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var query = PublicRequests(module)
            .FirstOrDefault(request => !typeof(ITransactionalRequest).IsAssignableFrom(request));

        Assert.NotNull(query);

        var boundaries = ResolveBehavioursFor(query!)
            .Where(behaviour => DerivesFromModuleBehaviour(behaviour!.GetType()))
            .ToList();

        Assert.True(
            boundaries.Count == 0,
            $"{query!.Name} is a query and must get no transaction; got "
            + string.Join(", ", boundaries.Select(behaviour => behaviour!.GetType().Name)));
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// The host's composition, assembled from the same public entry points Program.cs uses, so
    /// a module that forgets one of them fails here rather than at the first request.
    ///
    /// <para>
    /// A real <c>WebApplicationBuilder</c>, not a bare <c>ServiceCollection</c>. The first
    /// version used the latter and the container-build assertion failed on
    /// <c>AuthorizationPolicyCache</c>, which needs an <c>EndpointDataSource</c> that only a web
    /// host registers. The fix is the right one rather than the convenient one: a stub
    /// <c>EndpointDataSource</c> would have made the test pass while validating a graph the
    /// application never builds, and the whole point of this file is that the thing asserted and
    /// the thing deployed are the same thing.
    /// </para>
    /// </summary>
    private static IServiceCollection ComposeHost()
    {
        var builder = WebApplication.CreateBuilder();
        var services = builder.Services;

        services.AddBoomboCardSharedServices();

        // The host's half of the composition: the authentication scheme, the authorization
        // policies, the rate limiter, and the IAccessTokenIssuer the Customers module declares
        // but deliberately does not implement. Included so the container-build assertion
        // exercises the real graph — without it a module whose only unmet dependency is one the
        // host supplies would look broken here and work in production.
        services.AddBoomboCardSecurity(HostConfiguration);

        foreach (var module in Modules.All)
        {
            module.Register(services, options => options.UseSqlServer(UnusedConnectionString));
        }

        services.AddMediatR(configuration => configuration.RegisterServicesFromAssemblies(
            Modules.All.Select(module => module.Module).Distinct().ToArray()));

        return services;
    }

    private static IEnumerable<object?> ResolveBehavioursFor(Type request)
    {
        var responseType = request.GetInterfaces()
            .Single(contract => contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IRequest<>))
            .GetGenericArguments()[0];

        var pipelineInterface = typeof(IPipelineBehavior<,>).MakeGenericType(request, responseType);

        using var provider = ComposeHost().BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();

        return scope.ServiceProvider.GetServices(pipelineInterface).ToList();
    }

    private static IEnumerable<Type> AllTransactionalRequests() =>
        Modules.All
            .SelectMany(module => module.Module.GetTypes().Concat(module.Contracts.GetTypes()))
            .Distinct()
            .Where(type => typeof(ITransactionalRequest).IsAssignableFrom(type)
                && type is { IsInterface: false, IsAbstract: false });

    /// <summary>
    /// Public only: a query has to be reachable from the host to be worth resolving, and an
    /// internal one cannot be closed over from here.
    /// </summary>
    private static IEnumerable<Type> PublicRequests(ModuleUnderTest module) =>
        module.Contracts.GetExportedTypes()
            .Where(type => type is { IsInterface: false, IsAbstract: false })
            .Where(type => type.GetInterfaces().Any(contract =>
                contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IRequest<>)));

    private static bool DerivesFromModuleBehaviour(Type implementation)
    {
        for (var current = implementation; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType
                && current.GetGenericTypeDefinition() == typeof(ModuleUnitOfWorkBehavior<,>))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the generic constraint off the open-generic behaviour, which is the mechanism doing
    /// the routing: the container skips a registration whose constraints the closed type would
    /// violate, so the constraint is what keeps one module's commands out of another's
    /// transaction.
    /// </summary>
    private static bool ConstrainsRequestsTo(Type openBehaviour, Type marker) =>
        openBehaviour.IsGenericTypeDefinition
        && openBehaviour.GetGenericArguments()[0]
            .GetGenericParameterConstraints()
            .Contains(marker);
}
