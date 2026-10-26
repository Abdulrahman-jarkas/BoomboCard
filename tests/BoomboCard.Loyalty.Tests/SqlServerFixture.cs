using BoomboCard.Loyalty.Data;
using BoomboCard.SharedKernel.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Loyalty.Tests;

/// <summary>
/// A real SQL Server LocalDB database, created fresh per test class and dropped afterwards.
///
/// <para>
/// The balance invariant here is carried by a concurrency token and the idempotency by a unique
/// index, and the InMemory provider has neither (Onyx <b>T6</b>) — so every race test below
/// would pass without exercising anything.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"BoomboCardLoyaltyTests_{Guid.NewGuid():N}";

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
    internal LoyaltyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LoyaltyDbContext>()
            .UseSqlServer(ConnectionString)
            .UseBoomboCardConventions()
            .AddInterceptors(new VersioningInterceptor())
            .Options;

        return new LoyaltyDbContext(options);
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
