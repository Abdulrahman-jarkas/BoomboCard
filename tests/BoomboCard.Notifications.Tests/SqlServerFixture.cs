using BoomboCard.Notifications.Data;
using BoomboCard.SharedKernel.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Notifications.Tests;

/// <summary>
/// A real SQL Server LocalDB database, created fresh per test class and dropped afterwards.
///
/// <para>
/// This module's central guarantee — <c>UX_Notifications_Idempotency</c> on
/// <c>(Kind, Reference)</c>, which is what stops a redelivered event sending a customer a second
/// email — is a unique index. The InMemory provider has no unique indexes (Onyx <b>T6</b>), so a
/// test of it there would pass while asserting nothing, which is worse than no test.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"BoomboCardNotificationsTests_{Guid.NewGuid():N}";

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
    /// of them genuinely independent writers.
    /// </summary>
    internal NotificationsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseSqlServer(ConnectionString)
            .UseBoomboCardConventions()
            .AddInterceptors(new VersioningInterceptor())
            .Options;

        return new NotificationsDbContext(options);
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
