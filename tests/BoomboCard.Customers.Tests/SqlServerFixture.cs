using BoomboCard.Customers.Data;
using BoomboCard.SharedKernel.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Customers.Tests;

/// <summary>
/// A real SQL Server LocalDB database, created fresh per test class and dropped afterwards.
///
/// <para>
/// The same engine the application runs on. Not the InMemory provider — Onyx T6 tested its CSV
/// import against <c>UseInMemoryDatabase</c>, which enforces no unique indexes, no constraints,
/// no transactions and no concurrency tokens, so it could not have detected the missing email
/// uniqueness (D10) or the single-use token race even in principle. And not SQLite either: three
/// of the eight bugs in <c>docs/BUGS-AND-LESSONS.md</c> were provider behaviour rather than
/// logic.
/// </para>
///
/// <para>
/// Pure unit tests — the aggregate, the value objects, the architecture rules — touch no
/// database and need none.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"BoomboCardCustomerTests_{Guid.NewGuid():N}";

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
    internal CustomersDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CustomersDbContext>()
            .UseSqlServer(ConnectionString)
            // The same conventions the host applies, so a query that would throw in production
            // throws here too rather than passing in tests and failing on deployment.
            .UseBoomboCardConventions()
            .AddInterceptors(new VersioningInterceptor())
            .Options;

        return new CustomersDbContext(options);
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
