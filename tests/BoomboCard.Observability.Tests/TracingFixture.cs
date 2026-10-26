using System.Diagnostics;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Observability;
using BoomboCard.SharedKernel.Persistence;
using BoomboCard.SharedKernel.Persistence.Inbox;
using BoomboCard.SharedKernel.Persistence.Outbox;
using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Observability.Tests;

/// <summary>
/// A stand-in module: one <c>DbContext</c> carrying the shared outbox and inbox, assembled from
/// the same pieces <c>AddModulePersistence</c> gives a real one.
///
/// <para>
/// Deliberately not one of the seven real modules. The property under test belongs to
/// <c>SharedKernel.Persistence</c> and every module inherits it identically, so pinning the test
/// to Customers would tie a shared guarantee to one module's internals and mean widening its
/// <c>InternalsVisibleTo</c> to reach them.
/// </para>
/// </summary>
public sealed class TracingDbContext(DbContextOptions<TracingDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
        modelBuilder.UseClientGeneratedKeys();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // The 500-character string convention every real module applies. Present because it is
        // what the outbox's own column lengths have to survive — it is the convention that
        // silently produced `Payload nvarchar(500)` once already, and TraceState asks for 512.
        configurationBuilder.Properties<string>().HaveMaxLength(500);
    }
}

/// <summary>
/// A real SQL Server LocalDB database per test class, matching every other suite here.
///
/// <para>
/// Not the InMemory provider, and the reason is specific rather than habitual: the propagation
/// test asserts that a <c>traceparent</c> written by one DbContext is read back by another, and
/// InMemory would store it as a .NET string in a dictionary. That proves nothing about the
/// <c>nvarchar(55)</c> it actually has to fit in — which is the half of this feature that can
/// break in production and not in a test.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"BoomboCardObservabilityTests_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        ConnectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={_databaseName};"
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

    /// <summary>A new context per call, which is the same shape as a new DI scope per request.</summary>
    public TracingDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TracingDbContext>()
            .UseSqlServer(ConnectionString)
            .Options);

    private static async Task ExecuteOnMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Subscribes to the solution's <see cref="ActivitySource"/> and keeps every span it produces.
///
/// <para>
/// A listener is not optional scaffolding. <c>ActivitySource.StartActivity</c> returns
/// <c>null</c> when nothing is listening — which is exactly how the production code behaves with
/// tracing switched off, and means that without this a test asserting "the trace continued"
/// would pass trivially against no activities at all. <see cref="Recorded"/> is also how these
/// tests check the <i>shape</i> of the trace rather than just its presence.
/// </para>
/// </summary>
public sealed class ActivityRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _recorded = [];
    private readonly List<(Func<Activity, bool> Predicate, TaskCompletionSource Signal)> _waiters = [];
    private readonly Lock _gate = new();

    public ActivityRecorder()
    {
        // The host does this in ObservabilityComposition. Repeated here because the format
        // decides whether a traceparent can be written at all: TraceContext refuses to serialise
        // a hierarchical activity, so a test running under the legacy default would silently
        // exercise the "no context captured" path and still look green.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BoomboCardTelemetry.ActivitySourceName,

            // AllDataAndRecorded, so the Recorded flag is set on the context and therefore on
            // the traceparent written to the row. Without it the stored flags would read "00"
            // and a ParentBased sampler downstream would correctly drop the continuation — the
            // precise way sampling tears a flow in half.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,

            ActivityStopped = activity =>
            {
                List<TaskCompletionSource> released;

                lock (_gate)
                {
                    _recorded.Add(activity);

                    released = _waiters
                        .Where(waiter => waiter.Predicate(activity))
                        .Select(waiter => waiter.Signal)
                        .ToList();

                    _waiters.RemoveAll(waiter => waiter.Predicate(activity));
                }

                // Outside the lock: the continuation runs test code, and holding the gate
                // across it would deadlock against Recorded.
                foreach (var signal in released)
                {
                    signal.TrySetResult();
                }
            }
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Recorded
    {
        get
        {
            lock (_gate)
            {
                return [.. _recorded];
            }
        }
    }

    public Activity Single(string name) =>
        Recorded.Single(activity => activity.OperationName == name);

    /// <summary>
    /// Completes once a span with this name has <b>ended</b>, including one that ended before
    /// this was called.
    ///
    /// <para>
    /// Waiting on a span's end rather than on a callback inside it is what makes the dispatcher
    /// tests deterministic. The obvious alternative — signal from the publisher — releases the
    /// test in the middle of the pass, before the dispatcher has marked the message and saved;
    /// the test then stops the host, and <c>StopAsync</c> cancels the token that very
    /// <c>SaveChangesAsync</c> is using, so the row is still Pending when the assertion reads
    /// it. The pass span closes after the save, so it is the honest "this pass is finished"
    /// signal.
    /// </para>
    /// </summary>
    public Task WaitForSpanAsync(string operationName)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_recorded.Any(activity => activity.OperationName == operationName))
            {
                return Task.CompletedTask;
            }

            _waiters.Add((activity => activity.OperationName == operationName, signal));
        }

        return signal.Task;
    }

    public void Dispose() => _listener.Dispose();
}

// ----------------------------------------------------------------- doubles

/// <summary>An integration event with nothing in it but the two fields the contract requires.</summary>
public sealed record TestIntegrationEvent(Guid EventId, DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// Stands in for MediatR, and records the ambient trace context at the moment a consumer would
/// run — which is the single value these tests exist to assert.
/// </summary>
public sealed class RecordingPublisher : IPublisher
{
    public List<ActivityContext> ObservedContexts { get; } = [];

    public Func<object, Task>? OnPublish { get; set; }

    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        ObservedContexts.Add(Activity.Current?.Context ?? default);

        return OnPublish?.Invoke(notification) ?? Task.CompletedTask;
    }

    public Task Publish<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification =>
        Publish((object)notification!, cancellationToken);
}
