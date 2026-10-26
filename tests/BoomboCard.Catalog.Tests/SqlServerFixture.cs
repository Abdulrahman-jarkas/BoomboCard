using BoomboCard.Catalog.Data;
using BoomboCard.SharedKernel.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Tests;

/// <summary>
/// A real SQL Server LocalDB database, created fresh per test class and dropped afterwards.
///
/// <para>
/// <b>Why not the InMemory provider, and why not SQLite any more.</b> Onyx T6 tested its CSV
/// import against <c>UseInMemoryDatabase</c>, which enforces no unique indexes, no constraints,
/// no transactions and no concurrency tokens — so it could not have detected the missing SKU
/// uniqueness (D10) or the stock race (D1) even in principle.
/// </para>
///
/// <para>
/// SQLite was better, but it is still not what production runs. Three of the five bugs in
/// docs/BUGS-AND-LESSONS.md were provider behaviour, not logic: one was SQLite refusing to order
/// <c>DateTimeOffset</c>, which does not exist on SQL Server at all. A test suite that passes on
/// a database nobody deploys is testing a different system.
/// </para>
///
/// <para>
/// So these run on the same engine the application does. Pure unit tests — the aggregate, value
/// objects, architecture rules — touch no database and need none.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"BoomboCardTests_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={_databaseName};"
            + "Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true";

        await ExecuteOnMasterAsync($"CREATE DATABASE [{_databaseName}]");

        // The schema, from the model. Migrations live in a provider-specific assembly the test
        // project does not reference, so the schema is created from the model here. The migration
        // itself is verified by the application starting against it.
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();

        await ExecuteOnMasterAsync(
            $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
            + $"DROP DATABASE [{_databaseName}]");
    }

    /// <summary>
    /// A new context per call, which is the same shape as a new DI scope per request — and what
    /// makes two of them genuinely independent writers for the concurrency tests.
    /// </summary>
    internal CatalogDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(ConnectionString)
            // The same conventions the host applies, so a query that would throw in production
            // throws here too rather than passing in tests and failing on deployment.
            .UseBoomboCardConventions()
            .AddInterceptors(new VersioningInterceptor())
            .Options;

        return new CatalogDbContext(options);
    }

    private static async Task ExecuteOnMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
