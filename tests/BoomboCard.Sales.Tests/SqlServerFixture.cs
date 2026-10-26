using BoomboCard.Sales.Data;
using BoomboCard.SharedKernel.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Sales.Tests;

/// <summary>
/// A real SQL Server LocalDB database, created fresh per test class and dropped afterwards.
///
/// <para>
/// Sales is the module where the InMemory provider would be most tempting and most useless. The
/// order number's uniqueness is a unique index, confirm idempotency is a unique index on the
/// session id, duplicate-payment protection is a unique index on the capture reference, and the
/// number sequence is guarded by a concurrency token. The InMemory provider has none of those
/// (Onyx <b>T6</b>), so every test below would pass while asserting nothing.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"BoomboCardSalesTests_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString =
            $@"Server=(localdb)\MSSQLLocalDB;Database={_databaseName};"
            + "Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true";

        await ExecuteOnMasterAsync($"CREATE DATABASE [{_databaseName}]");

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
    /// A new context per call — the same shape as a new DI scope per request, and what makes two
    /// of them genuinely independent writers for the concurrency tests.
    /// </summary>
    internal SalesDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseSqlServer(ConnectionString)
            .UseBoomboCardConventions()
            .AddInterceptors(new VersioningInterceptor())
            .Options;

        return new SalesDbContext(options);
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
