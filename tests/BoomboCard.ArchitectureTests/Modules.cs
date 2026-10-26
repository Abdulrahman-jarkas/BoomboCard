using System.Reflection;
using BoomboCard.Catalog;
using BoomboCard.Customers;
using BoomboCard.Loyalty;
using BoomboCard.Notifications;
using BoomboCard.Payments;
using BoomboCard.Promotions;
using BoomboCard.Sales;
using BoomboCard.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoomboCard.ArchitectureTests;

/// <summary>
/// One module, as the architecture rules see it: its two assemblies, its namespaces, and the
/// marker interface its commands carry.
///
/// Namespaces are derived rather than configured, which makes the naming convention itself
/// load-bearing — a module that puts its aggregates somewhere other than
/// <c>BoomboCard.X.Domain</c> fails the layering tests instead of silently escaping them.
/// </summary>
internal sealed record ModuleUnderTest(
    string Name,
    Assembly Module,
    Assembly Contracts,
    Type RequestMarker,
    Type DbContextType,
    Action<IServiceCollection, Action<DbContextOptionsBuilder>> Register)
{
    public string RootNamespace => $"BoomboCard.{Name}";

    public string DomainNamespace => $"{RootNamespace}.Domain";

    public string DataNamespace => $"{RootNamespace}.Data";

    public string ApplicationNamespace => $"{RootNamespace}.Application";

    public string EndpointsNamespace => $"{RootNamespace}.Endpoints";

    /// <summary>xUnit prints this in the test name, so a failure says which module failed.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The registry. **Adding a module means adding one line here**, and it immediately inherits
/// every layering, encapsulation, event-shape and wiring rule in this project.
///
/// This is the structural answer to Onyx X1–X15, where fifteen specific claims in
/// <c>ARCHITECTURE.md</c> were contradicted by the code. A prose document cannot notice a new
/// project that breaks its rules. This can, and does so without anybody remembering to extend it.
/// </summary>
internal static class Modules
{
    public static readonly Assembly SharedKernel = typeof(AggregateRoot).Assembly;

    public static readonly Assembly SharedKernelPersistence =
        typeof(SharedKernel.Persistence.EfUnitOfWork<>).Assembly;

    public static readonly IReadOnlyList<ModuleUnderTest> All =
    [
        new(
            Name: "Catalog",
            Module: typeof(Catalog.DependencyInjection).Assembly,
            Contracts: typeof(Catalog.Contracts.ICatalogRequest).Assembly,
            RequestMarker: typeof(Catalog.Contracts.ICatalogRequest),

            // Reached by name because the context is internal to the module — which is itself
            // the point being asserted by Persistence_TypesAreNotPubliclyVisibleOutsideTheModule.
            DbContextType: typeof(Catalog.DependencyInjection).Assembly
                .GetType("BoomboCard.Catalog.Data.CatalogDbContext")!,

            Register: (services, configureDbContext) =>
                services.AddCatalogModule(configureDbContext)),

        new(
            Name: "Customers",
            Module: typeof(Customers.DependencyInjection).Assembly,
            Contracts: typeof(Customers.Contracts.ICustomersRequest).Assembly,
            RequestMarker: typeof(Customers.Contracts.ICustomersRequest),
            DbContextType: typeof(Customers.DependencyInjection).Assembly
                .GetType("BoomboCard.Customers.Data.CustomersDbContext")!,
            Register: (services, configureDbContext) =>
                services.AddCustomersModule(configureDbContext, CustomersConfiguration)),

        new(
            Name: "Promotions",
            Module: typeof(Promotions.DependencyInjection).Assembly,
            Contracts: typeof(Promotions.Contracts.IPromotionsRequest).Assembly,
            RequestMarker: typeof(Promotions.Contracts.IPromotionsRequest),
            DbContextType: typeof(Promotions.DependencyInjection).Assembly
                .GetType("BoomboCard.Promotions.Data.PromotionsDbContext")!,
            Register: (services, configureDbContext) =>
                services.AddPromotionsModule(configureDbContext)),

        new(
            Name: "Loyalty",
            Module: typeof(Loyalty.DependencyInjection).Assembly,
            Contracts: typeof(Loyalty.Contracts.ILoyaltyRequest).Assembly,
            RequestMarker: typeof(Loyalty.Contracts.ILoyaltyRequest),
            DbContextType: typeof(Loyalty.DependencyInjection).Assembly
                .GetType("BoomboCard.Loyalty.Data.LoyaltyDbContext")!,
            Register: (services, configureDbContext) =>
                services.AddLoyaltyModule(configureDbContext)),

        new(
            Name: "Sales",
            Module: typeof(Sales.DependencyInjection).Assembly,
            Contracts: typeof(Sales.Contracts.ISalesRequest).Assembly,
            RequestMarker: typeof(Sales.Contracts.ISalesRequest),
            DbContextType: typeof(Sales.DependencyInjection).Assembly
                .GetType("BoomboCard.Sales.Data.SalesDbContext")!,
            Register: (services, configureDbContext) =>
                services.AddSalesModule(configureDbContext)),

        new(
            Name: "Payments",
            Module: typeof(Payments.DependencyInjection).Assembly,
            Contracts: typeof(Payments.Contracts.IPaymentsRequest).Assembly,
            RequestMarker: typeof(Payments.Contracts.IPaymentsRequest),
            DbContextType: typeof(Payments.DependencyInjection).Assembly
                .GetType("BoomboCard.Payments.Data.PaymentsDbContext")!,
            Register: (services, configureDbContext) =>
            {
                services.AddPaymentsModule(configureDbContext);

                // The gateway is the HOST's to provide. IPaymentGateway is declared by the module
                // and deliberately not registered by it — the base address, the API key, the
                // retry policy and the TLS configuration are deployment concerns, exactly as the
                // JWT signing key is for IAccessTokenIssuer.
                //
                // The container is built with ValidateOnBuild, so without this the wiring test
                // would fail for a correct reason at a misleading moment.
                services.AddSingleton<Payments.Domain.Gateway.IPaymentGateway, UnusedGateway>();
            }),

        new(
            Name: "Notifications",
            Module: typeof(Notifications.DependencyInjection).Assembly,
            Contracts: typeof(Notifications.Contracts.INotificationsRequest).Assembly,
            RequestMarker: typeof(Notifications.Contracts.INotificationsRequest),
            DbContextType: typeof(Notifications.DependencyInjection).Assembly
                .GetType("BoomboCard.Notifications.Data.NotificationsDbContext")!,
            Register: (services, configureDbContext) =>
            {
                services.AddNotificationsModule(configureDbContext);

                // The sender is the HOST's to provide, as the payment gateway is. Same reasoning:
                // the provider, its credential and its timeout are deployment concerns.
                services.AddSingleton<
                    Notifications.Domain.Sending.INotificationSender, UnusedSender>();
            })
    ];

    /// <summary>
    /// Stands in for the host's notification sender so the container can be built.
    ///
    /// <para>
    /// Throws rather than returning <c>Accepted</c>, for the reason <see cref="UnusedGateway"/>
    /// throws: a double that quietly reports success would be the one thing in this codebase
    /// shaped like Onyx <b>A8</b> — every message recorded as delivered, nothing transmitted — and
    /// a test double is still code someone may copy.
    /// </para>
    /// </summary>
    private sealed class UnusedSender : Notifications.Domain.Sending.INotificationSender
    {
        public string Name => "unused";

        public Task<Notifications.Domain.Sending.SendResult> SendAsync(
            Notifications.Domain.Sending.NotificationEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Stands in for the host's payment gateway so the container can be built.
    ///
    /// <para>
    /// Never called: these tests assert wiring, not behaviour. It throws rather than returning a
    /// plausible result, because a gateway that quietly answered "approved" would be the one
    /// thing in this codebase shaped like Onyx <b>M1</b>, and a test double is still code someone
    /// may copy.
    /// </para>
    /// </summary>
    private sealed class UnusedGateway : Payments.Domain.Gateway.IPaymentGateway
    {
        public Task<Payments.Domain.Gateway.GatewayResult> AuthorizeAsync(
            Payments.Domain.Gateway.GatewayAuthorizeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Payments.Domain.Gateway.GatewayResult> CaptureAsync(
            Payments.Domain.Gateway.GatewayCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Payments.Domain.Gateway.GatewayResult> RefundAsync(
            Payments.Domain.Gateway.GatewayRefundRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Payments.Domain.Gateway.GatewayResult> EnquireAsync(
            Payments.Domain.Gateway.GatewayEnquiryRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// The minimum configuration <c>AddCustomersModule</c> refuses to start without.
    ///
    /// <c>CustomersOptions.Validate()</c> throws on a missing <c>ClientFingerprintKey</c>,
    /// deliberately — see <c>ClientFingerprint</c> for why an unkeyed hash of an IP address is
    /// not anonymous. Supplying it here rather than weakening the check is the point: the
    /// fail-fast behaviour is itself asserted, by <c>SecurityCompositionTests</c>.
    /// </summary>
    private static readonly IConfiguration CustomersConfiguration =
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Customers:ClientFingerprintKey"] = "architecture-tests-fingerprint-key"
            })
            .Build();

    /// <summary>Feeds the theories. One case per module, per rule.</summary>
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();

        foreach (var module in All)
        {
            data.Add(module.Name);
        }

        return data;
    }

    public static ModuleUnderTest ByName(string name) =>
        All.Single(module => module.Name == name);
}
