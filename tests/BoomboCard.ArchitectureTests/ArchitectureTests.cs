using System.Reflection;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Inbox;
using BoomboCard.SharedKernel.Persistence.Outbox;
using NetArchTest.Rules;

namespace BoomboCard.ArchitectureTests;

/// <summary>
/// The layering rules, as assertions rather than prose, applied to every module in
/// <see cref="Modules.All"/>.
///
/// This exists because of Onyx X1–X15. Its <c>ARCHITECTURE.md</c> made fifteen specific claims
/// that its own code contradicted — "Onyx.Domain… No EF Core, no ASP.NET Core, no knowledge of
/// HTTP or SQL" while the project file referenced EF Core and the pricing service took an
/// <c>IQueryable</c>; "repositories are the seam, controllers don't reach into EF Core directly"
/// while all four controllers injected the DbContext; "stateless at the process level" while
/// four pieces of mutable static state held the system together.
///
/// A design document cannot enforce anything. These can, and they fail the build when a rule
/// stops being true — for a module written next year as much as for the one written today.
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>
    /// Module names rather than module objects: a theory parameter has to be serialisable for
    /// xUnit to name and re-run the case, and a string is. The registry turns it back into a
    /// <see cref="ModuleUnderTest"/>.
    /// </summary>
    public static TheoryData<string> AllModules => Modules.Names();

    // ----------------------------------------------------------------- the shared kernel

    /// <summary>
    /// The claim Onyx X7 made and broke. Here the project file has no such reference, and this
    /// asserts it stays that way.
    /// </summary>
    [Fact]
    public void SharedKernel_DoesNotDependOnInfrastructure()
    {
        var result = Types.InAssembly(Modules.SharedKernel)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Microsoft.Data",
                "Npgsql",
                "System.Data.SqlClient")
            .GetResult();

        AssertSuccess(result, "SharedKernel must stay free of EF Core and ASP.NET Core.");
    }

    /// <summary>
    /// The SharedKernel must not know any module exists. It is the thing every module depends
    /// on, so a reference in this direction would make the dependency graph a cycle and the
    /// module boundaries decorative.
    /// </summary>
    [Fact]
    public void SharedKernel_DoesNotDependOnAnyModule()
    {
        var moduleNamespaces = Modules.All.Select(module => module.RootNamespace).ToArray();

        foreach (var assembly in new[] { Modules.SharedKernel, Modules.SharedKernelPersistence })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(moduleNamespaces)
                .GetResult();

            AssertSuccess(result, $"{assembly.GetName().Name} must not reference any module.");
        }
    }

    // ----------------------------------------------------------------- the domain layer

    [Theory]
    [MemberData(nameof(AllModules))]
    public void Domain_DoesNotDependOnEntityFrameworkOrAspNet(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var result = Types.InAssembly(module.Module)
            .That().ResideInNamespace(module.DomainNamespace)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "CsvHelper")
            .GetResult();

        AssertSuccess(result, "The domain must not know about persistence, HTTP or file formats.");
    }

    /// <summary>
    /// The dependency arrow points inward only. The domain is the thing everything else depends
    /// on; it depends on nothing above it.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Domain_DoesNotDependOnOuterLayers(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var result = Types.InAssembly(module.Module)
            .That().ResideInNamespace(module.DomainNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(
                module.DataNamespace,
                module.ApplicationNamespace,
                module.EndpointsNamespace)
            .GetResult();

        AssertSuccess(result, "The domain must not depend on Data, Application or Endpoints.");
    }

    /// <summary>
    /// Onyx A11: its pricing service took an <c>IQueryable&lt;Order&gt;</c> and called
    /// <c>EF.Functions.Like</c>, which is a SQL translation concern wearing a domain service's
    /// name.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Domain_DoesNotDependOnMediatRRequests(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var result = Types.InAssembly(module.Module)
            .That().ResideInNamespace(module.DomainNamespace)
            .And().DoNotHaveNameEndingWith("Event")
            .And().DoNotResideInNamespaceContaining("Events")
            .ShouldNot()
            .HaveDependencyOn("MediatR")
            .GetResult();

        AssertSuccess(
            result,
            "Only domain events may touch MediatR, and only because IDomainEvent extends "
            + "INotification. Nothing else in the domain may know about the mediator.");
    }

    // ----------------------------------------------------------------- module encapsulation

    /// <summary>
    /// The boundary that makes this a modular monolith rather than a monolith with folders.
    /// Another module physically cannot reference <c>Product</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Persistence_TypesAreNotPubliclyVisibleOutsideTheModule(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var leaked = module.Module.GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith(module.DataNamespace, StringComparison.Ordinal) == true)
            // EF generates migration classes as public and regenerates them on demand, so they
            // are not hand-written code to hold to this rule.
            .Where(type => type.Namespace?.Contains(".Migrations", StringComparison.Ordinal) != true)
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            leaked.Count == 0,
            "Persistence types must be internal to the module. Public: " + string.Join(", ", leaked));
    }

    [Theory]
    [MemberData(nameof(AllModules))]
    public void Handlers_AreInternal(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var publicHandlers = module.Module.GetExportedTypes()
            .Where(type => type.Name.EndsWith("Handler", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            publicHandlers.Count == 0,
            "Handlers are an implementation detail reached through the mediator, so they must be "
            + "internal. Public: " + string.Join(", ", publicHandlers));
    }

    /// <summary>
    /// Contracts is the module's published surface, so it must stay free of infrastructure — a
    /// consumer referencing it should not transitively acquire EF Core.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Contracts_DoNotDependOnInfrastructureOrTheModule(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var result = Types.InAssembly(module.Contracts)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                module.DomainNamespace,
                module.DataNamespace,
                module.ApplicationNamespace)
            .GetResult();

        AssertSuccess(result, "Contracts must carry primitives only, with no infrastructure.");
    }

    /// <summary>
    /// A module may only see another module through its Contracts assembly. Referencing the
    /// module project directly would give it the other's aggregates, repositories and DbContext
    /// — at which point the boundary is a folder name.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Modules_SeeEachOtherOnlyThroughContracts(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var forbidden = Modules.All
            .Where(other => other.Name != module.Name)
            .SelectMany(other => new[]
            {
                other.DomainNamespace,
                other.DataNamespace,
                other.ApplicationNamespace,
                other.EndpointsNamespace
            })
            .ToArray();

        if (forbidden.Length == 0)
        {
            return;
        }

        var result = Types.InAssembly(module.Module)
            .ShouldNot()
            .HaveDependencyOnAny(forbidden)
            .GetResult();

        AssertSuccess(
            result,
            $"{module.Name} may only reach another module through its Contracts assembly.");
    }

    // ----------------------------------------------------------------- domain modelling rules

    [Theory]
    [MemberData(nameof(AllModules))]
    public void DomainEvents_AreImmutableRecords(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var mutable = EventTypes(module)
            .Where(type => typeof(IDomainEvent).IsAssignableFrom(type))
            .Where(type => !IsRecord(type) || HasSettableProperty(type))
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            mutable.Count == 0,
            "A domain event is a record of something that happened, so it must be an immutable "
            + "record. Offenders: " + string.Join(", ", mutable));
    }

    /// <summary>
    /// dome-gym's <c>GymAddedEvent(Subscription, Gym)</c> and RiverBooks'
    /// <c>OrderCreatedEvent(Order)</c> both carry live aggregates: unserialisable, so they can
    /// never go through an outbox, and mutable, so a handler can observe state that moved on
    /// after the fact was recorded.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Events_CarryIdsAndPrimitives_NotAggregates(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var offenders = new List<string>();

        foreach (var eventType in EventTypes(module))
        {
            foreach (var property in eventType.GetProperties())
            {
                if (typeof(Entity).IsAssignableFrom(property.PropertyType))
                {
                    offenders.Add($"{eventType.Name}.{property.Name} ({property.PropertyType.Name})");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Events must carry ids and primitives, never entity or aggregate instances. "
            + "Offenders: " + string.Join(", ", offenders));
    }

    [Theory]
    [MemberData(nameof(AllModules))]
    public void IntegrationEvents_LiveInContracts_SoOtherModulesCanSeeThem(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var misplaced = module.Module.GetTypes()
            .Where(type => typeof(IIntegrationEvent).IsAssignableFrom(type)
                && type is { IsInterface: false, IsAbstract: false })
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            misplaced.Count == 0,
            $"Integration events are a published contract, so they belong in "
            + $"{module.Contracts.GetName().Name}, not inside the module. Offenders: "
            + string.Join(", ", misplaced));
    }

    /// <summary>
    /// A collection on an aggregate must be exposed read-only, so the only way to change it is a
    /// method that can enforce the invariants.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Aggregates_DoNotExposeMutableCollections(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var offenders = new List<string>();

        foreach (var aggregate in Aggregates(module))
        {
            foreach (var property in aggregate.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyType = property.PropertyType;

                if (!propertyType.IsGenericType)
                {
                    continue;
                }

                var generic = propertyType.GetGenericTypeDefinition();

                if (generic == typeof(List<>) || generic == typeof(ICollection<>) || generic == typeof(HashSet<>))
                {
                    offenders.Add($"{aggregate.Name}.{property.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Expose collections as IReadOnlyCollection over a private field. Offenders: "
            + string.Join(", ", offenders));
    }

    [Theory]
    [MemberData(nameof(AllModules))]
    public void Aggregates_HaveAPrivateOrProtectedParameterlessConstructorForEfCore(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var offenders = Aggregates(module)
            .Where(type => type.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [],
                modifiers: null) is null)
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "EF needs a non-public parameterless constructor to materialise an aggregate. "
            + "Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The outbox and inbox are shared plumbing, so a module must not define its own. Onyx's
    /// equivalent of "every module solves this its own way" was four pieces of cross-process
    /// static state standing in for one durable table (A7, D8, D9, X3).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllModules))]
    public void Modules_DoNotDefineTheirOwnOutboxOrInbox(string moduleName)
    {
        var module = Modules.ByName(moduleName);

        var duplicates = module.Module.GetTypes()
            .Where(type => type != typeof(OutboxMessage) && type != typeof(InboxMessage))
            .Where(type => type.Name is "OutboxMessage" or "InboxMessage"
                or "OutboxWriter" or "InboxGuard")
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "The outbox and inbox live in SharedKernel.Persistence and are shared. Offenders: "
            + string.Join(", ", duplicates));
    }

    // ----------------------------------------------------------------- helpers

    private static IEnumerable<Type> EventTypes(ModuleUnderTest module) =>
        module.Module.GetTypes()
            .Concat(module.Contracts.GetTypes())
            .Where(type =>
                (typeof(IDomainEvent).IsAssignableFrom(type) || typeof(IIntegrationEvent).IsAssignableFrom(type))
                && type is { IsInterface: false, IsAbstract: false });

    private static IEnumerable<Type> Aggregates(ModuleUnderTest module) =>
        module.Module.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(AggregateRoot)) && !type.IsAbstract);

    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null
        || type.GetMethods().Any(method => method.Name == "op_Equality" && method.IsStatic);

    private static bool HasSettableProperty(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.SetMethod is { IsPublic: true } setter
                && !setter.ReturnParameter.GetRequiredCustomModifiers()
                    .Any(modifier => modifier.Name == "IsExternalInit"));

    private static void AssertSuccess(TestResult result, string because)
    {
        var failing = result.FailingTypeNames ?? [];

        Assert.True(
            result.IsSuccessful,
            $"{because}{Environment.NewLine}Violations:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", failing));
    }
}
