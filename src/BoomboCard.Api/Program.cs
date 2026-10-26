using BoomboCard.Api.Authentication;
using BoomboCard.Api.OpenApi;
using BoomboCard.Api.Notifications;
using BoomboCard.Api.Observability;
using BoomboCard.Api.Payments;
using BoomboCard.Catalog;
using BoomboCard.Catalog.Endpoints;
using BoomboCard.Customers;
using BoomboCard.Customers.Endpoints;
using BoomboCard.Loyalty;
using BoomboCard.Loyalty.Endpoints;
using BoomboCard.Promotions;
using BoomboCard.Notifications;
using BoomboCard.Notifications.Endpoints;
using BoomboCard.Payments;
using BoomboCard.Payments.Endpoints;
using BoomboCard.Promotions.Endpoints;
using BoomboCard.Sales;
using BoomboCard.Sales.Endpoints;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;

// Each module owns its schema name; the host needs them only to place each module's
// migrations-history table in its own schema, so the migration histories stay separate.
const string CatalogSchema = "catalog";
const string CatalogMigrationsAssembly = "BoomboCard.Catalog.Migrations.SqlServer";
const string CustomersSchema = "customers";
const string CustomersMigrationsAssembly = "BoomboCard.Customers.Migrations.SqlServer";
const string PromotionsSchema = "promotions";
const string PromotionsMigrationsAssembly = "BoomboCard.Promotions.Migrations.SqlServer";
const string LoyaltySchema = "loyalty";
const string LoyaltyMigrationsAssembly = "BoomboCard.Loyalty.Migrations.SqlServer";
const string SalesSchema = "sales";
const string SalesMigrationsAssembly = "BoomboCard.Sales.Migrations.SqlServer";
const string PaymentsSchema = "payments";
const string PaymentsMigrationsAssembly = "BoomboCard.Payments.Migrations.SqlServer";
const string NotificationsSchema = "notifications";
const string NotificationsMigrationsAssembly = "BoomboCard.Notifications.Migrations.SqlServer";

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- composition root

// Onyx A2 switched both of these OFF to silence the validator that had correctly detected a
// singleton cache holding a scoped DbContext. They stay on here, in every environment, and
// there is a test asserting the container builds.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

// Enums as names, not integers: "Created" rather than 0. A response a human can read without a
// lookup table.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Onyx A18/A21: an unhandled DivideByZeroException in the nightly report terminated the entire
// worker process under the default StopHost behaviour, taking the unrelated stock reconciliation
// job down with it — with nothing to restart either and a status panel that never worked.
//
// Ignore means a faulting job stops being retried but the host and every other job survive. Each
// job already has its own per-iteration boundary via JobRunner; this is the backstop for anything
// that throws outside one.
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Authentication, authorization and rate limiting, for the whole host. One scheme, one signing
// key, one set of limits — seven modules each configuring their own would be seven chances to
// configure one differently. Modules name policies; this defines what the names mean.
builder.Services.AddBoomboCardSecurity(builder.Configuration);

builder.Services.AddOpenApi(options =>
{
    // Document-level metadata: title, description, and the tag descriptions that carry the
    // operational contract (idempotency keys, the 202-then-poll shape, all-or-nothing imports).
    options.AddDocumentTransformer<CatalogDocumentTransformer>();
});
builder.Services.AddProblemDetails();

// OpenTelemetry, before the modules so the exporter exists for anything that starts early.
//
// Traces are stitched into ONE flow per business operation, across the asynchronous handoffs
// that would otherwise break them: a checkout's trace continues through the outbox into Sales,
// Payments, Loyalty and Notifications, even though each of those runs on a later timer tick in
// a different DI scope. The mechanism is a stored traceparent on the outbox row — see
// TraceContext and OutboxDispatcher.
//
// Run `docker compose -f docker-compose.observability.yml up -d` and open http://localhost:18888
// to watch it. See docs/OBSERVABILITY.md.
builder.Services.AddBoomboCardObservability(builder.Configuration, builder.Environment);

// Shared services: TimeProvider, the versioning interceptor, the tracing behaviour and the
// resilient command dispatcher. Registered once, not per module — and registered BEFORE the
// modules, so the tracing behaviour sits outside each module's transaction in the pipeline.
builder.Services.AddBoomboCardSharedServices();

// The module. The host picks the provider; the module knows nothing about it.
var catalogConnectionString = builder.Configuration.GetConnectionString("Catalog")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Catalog is required. Onyx S5 kept a live payment key and a production "
        + "database password in appsettings.json; fail fast on a missing setting instead of "
        + "falling back to a default.");

builder.Services.AddCatalogModule(
    options => options.UseSqlServer(catalogConnectionString, sqlServer =>
    {
        // Transient-fault retries are a provider capability, so they belong here with the
        // provider — not in the module.
        //
        // One interaction worth knowing: an execution strategy cannot wrap a user-initiated
        // transaction unless the whole operation is wrapped in the strategy. UnitOfWorkBehavior
        // opens its own transaction, so retries stay off until that is reconciled. Turning them
        // on without doing so throws at runtime, which is the kind of thing Onyx shipped.
        // Migrations live in a provider-specific assembly, because EF's generated snapshot
        // calls provider-specific APIs and cannot compile without the provider referenced.
        // Keeping them out of the module is what lets the module stay provider-agnostic.
        sqlServer.MigrationsAssembly(CatalogMigrationsAssembly);
        sqlServer.MigrationsHistoryTable("__CatalogMigrations", CatalogSchema);
        sqlServer.CommandTimeout(30);
    }),
    builder.Configuration);

var customersConnectionString = builder.Configuration.GetConnectionString("Customers")
    ?? throw new InvalidOperationException("ConnectionStrings:Customers is required.");

builder.Services.AddCustomersModule(
    options => options.UseSqlServer(customersConnectionString, sqlServer =>
    {
        sqlServer.MigrationsAssembly(CustomersMigrationsAssembly);
        sqlServer.MigrationsHistoryTable("__CustomersMigrations", CustomersSchema);
        sqlServer.CommandTimeout(30);
    }),
    builder.Configuration);

var promotionsConnectionString = builder.Configuration.GetConnectionString("Promotions")
    ?? throw new InvalidOperationException("ConnectionStrings:Promotions is required.");

builder.Services.AddPromotionsModule(
    options => options.UseSqlServer(promotionsConnectionString, sqlServer =>
    {
        sqlServer.MigrationsAssembly(PromotionsMigrationsAssembly);
        sqlServer.MigrationsHistoryTable("__PromotionsMigrations", PromotionsSchema);
        sqlServer.CommandTimeout(30);
    }),
    builder.Configuration);

var loyaltyConnectionString = builder.Configuration.GetConnectionString("Loyalty")
    ?? throw new InvalidOperationException("ConnectionStrings:Loyalty is required.");

builder.Services.AddLoyaltyModule(
    options => options.UseSqlServer(loyaltyConnectionString, sqlServer =>
    {
        sqlServer.MigrationsAssembly(LoyaltyMigrationsAssembly);
        sqlServer.MigrationsHistoryTable("__LoyaltyMigrations", LoyaltySchema);
        sqlServer.CommandTimeout(30);
    }),
    builder.Configuration);

var salesConnectionString = builder.Configuration.GetConnectionString("Sales")
    ?? throw new InvalidOperationException("ConnectionStrings:Sales is required.");

builder.Services.AddSalesModule(
    options => options.UseSqlServer(salesConnectionString, sqlServer =>
    {
        sqlServer.MigrationsAssembly(SalesMigrationsAssembly);
        sqlServer.MigrationsHistoryTable("__SalesMigrations", SalesSchema);
        sqlServer.CommandTimeout(30);
    }),
    builder.Configuration);

var paymentsConnectionString = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("ConnectionStrings:Payments is required.");

builder.Services.AddPaymentsModule(
    options => options.UseSqlServer(paymentsConnectionString, sqlServer =>
    {
        sqlServer.MigrationsAssembly(PaymentsMigrationsAssembly);
        sqlServer.MigrationsHistoryTable("__PaymentsMigrations", PaymentsSchema);
        sqlServer.CommandTimeout(30);
    }),
    builder.Configuration);

// The payment provider, which the Payments module declares and does not register.
//
// The base address, the API key, the webhook secret, the timeout, the retry policy and the TLS
// configuration are all deployment concerns — the same reasoning that puts IAccessTokenIssuer
// here. The module references no HTTP package at all, which is also why Onyx S8
// (ServerCertificateCustomValidationCallback = ... => true on the gateway client) cannot be
// written inside it: there is no handler type in scope. A source-convention test bans the
// pattern across the whole solution regardless.
builder.Services.AddPaymentGateway(builder.Configuration, builder.Environment.IsDevelopment());

var notificationsConnectionString = builder.Configuration.GetConnectionString("Notifications")
    ?? throw new InvalidOperationException("ConnectionStrings:Notifications is required.");

builder.Services.AddNotificationsModule(
    options => options.UseSqlServer(notificationsConnectionString, sqlServer =>
    {
        sqlServer.MigrationsAssembly(NotificationsMigrationsAssembly);
        sqlServer.MigrationsHistoryTable("__NotificationsMigrations", NotificationsSchema);
        sqlServer.CommandTimeout(30);
    }),
    builder.Configuration);

// The notification sender, which the Notifications module declares and does not register.
//
// In Development this is the logging sender, which the roadmap asks for by name "so the
// capability is reachable and tested rather than wired up separately per environment" — Onyx A32,
// where a tested, documented CSV import was called by nothing but the test project. The options
// validator refuses the logging sender outside Development, because a sender that records every
// message as delivered and transmits nothing is Onyx A8 reached deliberately.
builder.Services.AddNotificationSender(builder.Configuration, builder.Environment.IsDevelopment());

// MediatR scans every module assembly plus this one, so a handler in any module can receive a
// message from any other. Each module contributes its own assembly.
//
// Sales is where this registration stops being a formality: its confirm handler sends commands
// into three other modules and its reconciliation job queries a fourth, all resolved by message
// type through this one container. Nothing in Sales holds a reference to another module's
// handler — only to its Contracts.
builder.Services.AddMediatR(configuration =>
    configuration.RegisterServicesFromAssemblies(
        typeof(Program).Assembly,
        BoomboCard.Catalog.DependencyInjection.CatalogAssembly,
        BoomboCard.Customers.DependencyInjection.CustomersAssembly,
        BoomboCard.Promotions.DependencyInjection.PromotionsAssembly,
        BoomboCard.Loyalty.DependencyInjection.LoyaltyAssembly,
        BoomboCard.Sales.DependencyInjection.SalesAssembly,
        BoomboCard.Payments.DependencyInjection.PaymentsAssembly,
        BoomboCard.Notifications.DependencyInjection.NotificationsAssembly));

var app = builder.Build();

// ---------------------------------------------------------------- pipeline

// ProblemDetails for everything, with no exception detail leaking to the client. Onyx S10 wrote
// feature.Error.ToString() into the response body in ALL environments, leaking file paths, class
// names, SQL and connection details.
app.UseExceptionHandler();
app.UseStatusCodePages();

// Before authentication, so a flood of sign-in attempts is rejected before any password
// hashing happens. PBKDF2 at 100k iterations is deliberately expensive, which makes an
// unlimited login endpoint a CPU amplifier as well as a guessing oracle.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    // The document itself comes from the built-in Microsoft.AspNetCore.OpenApi, which in .NET 10
    // also reads XML doc comments — so the <summary> on every request and response record becomes
    // the schema description.
    app.MapOpenApi().AllowAnonymous();

    // Swagger UI only. Swashbuckle's own generator is not used, so there is one source of truth
    // for the document.
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "BoomboCard API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "BoomboCard API";
        options.DisplayRequestDuration();
        options.EnableTryItOutByDefault();

        // Collapsed by default: 20 operations expanded is a wall of text.
        options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
        options.DefaultModelsExpandDepth(1);
    });

    // So /swagger is discoverable without reading the startup log.
    app.MapGet("/", () => Results.Redirect("/swagger"))
        .AllowAnonymous()
        .ExcludeFromDescription();
}

// ---------------------------------------------------------------- migrations

// Migrations, not EnsureCreated. Onyx D12 called EnsureCreated() from two processes against one
// SQLite file: it does nothing if the database exists, so every schema change meant deleting the
// database and losing all data — and the two processes could race on first run.
//
// Applied here behind a development check only. In a deployed environment this is a separate,
// deliberate step (a migrator job), never something every starting instance races to do.
if (app.Environment.IsDevelopment())
{
    await app.Services.MigrateCatalogAsync();
    await app.Services.MigrateCustomersAsync();
    await app.Services.MigratePromotionsAsync();
    await app.Services.MigrateLoyaltyAsync();
    await app.Services.MigrateSalesAsync();
    await app.Services.MigratePaymentsAsync();
    await app.Services.MigrateNotificationsAsync();
}

// ---------------------------------------------------------------- routes

app.MapCatalogEndpoints();
app.MapCustomerEndpoints();
app.MapPromotionEndpoints();
app.MapLoyaltyEndpoints();
app.MapSalesEndpoints();
app.MapPaymentEndpoints();
app.MapPaymentWebhookEndpoint();
app.MapNotificationEndpoints();

// Anonymous on purpose, and one of the few routes that says so out loud — the global fallback
// policy requires an authenticated principal, so a liveness probe would otherwise get a 401.
app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
    .AllowAnonymous()
    .WithTags("Health")
    .WithName("Health")
    .WithSummary("Liveness. Returns 200 while the host is running.")
    .Produces<HealthResponse>();

app.Run();

/// <summary>Liveness response.</summary>
internal sealed record HealthResponse(string Status);
