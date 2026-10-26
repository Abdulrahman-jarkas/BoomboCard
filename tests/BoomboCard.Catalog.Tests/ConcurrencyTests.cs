using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Tests;

/// <summary>
/// The tests that prove the optimistic concurrency token actually works end to end.
///
/// Onyx T5 had tests for pricing, CSV import and hashing — and nothing at all for order
/// placement, payments, auth, caching, the worker or concurrency, which is precisely where
/// every Critical issue in its register lived. Overselling (D1) was its single worst data bug
/// and no test could have caught it.
///
/// These use a real SQLite database with the VersioningInterceptor wired up, because this
/// behaviour only exists at the database level. The InMemory provider enforces no concurrency
/// tokens at all (Onyx T6).
/// </summary>
public sealed class ConcurrencyTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T09:00:00Z");

    private CatalogDbContext CreateContext() => fixture.CreateContext();

    // ----------------------------------------------------------------- the regression

    /// <summary>
    /// An uncontended write must not raise a concurrency exception.
    ///
    /// This failed the first time I ran the API: the interceptor mutated the POCO property
    /// directly, but DetectChanges had already run, so EF never learned about the new value.
    /// The resulting UPDATE had a SET and a WHERE that disagreed, affected zero rows, and threw
    /// DbUpdateConcurrencyException on a completely uncontended request.
    /// </summary>
    [Fact]
    public async Task SequentialWrites_DoNotConflict_AndTheVersionAdvancesEachTime()
    {
        var productId = await SeedActiveProductAsync(stock: 10);

        for (var expectedVersion = 1u; expectedVersion <= 3u; expectedVersion++)
        {
            await using var context = CreateContext();

            var product = await context.Set<Product>()
                .Include(candidate => candidate.Prices)
                .SingleAsync(candidate => candidate.Id == productId);

            var movement = product.Deduct(1, StockReason.Sale, $"session-{expectedVersion}", Now);
            Assert.False(movement.IsError);
            context.Add(movement.Value);

            await context.SaveChangesAsync();

            Assert.Equal(expectedVersion, product.Version);
        }

        await using var verify = CreateContext();
        var final = await verify.Set<Product>().SingleAsync(product => product.Id == productId);

        Assert.Equal(7, final.Stock.Quantity);
        Assert.Equal(3u, final.Version);
    }

    // ----------------------------------------------------------------- the oversell guard

    /// <summary>
    /// Two writers read the same version; the second to write loses.
    ///
    /// This is the exact Onyx D1 scenario. There, both read stock 3, both passed the check, and
    /// both wrote 1 — four units sold from three, with no error and no trace. Here the second
    /// writer's version predicate matches zero rows.
    /// </summary>
    [Fact]
    public async Task TwoWritersFromTheSameVersion_SecondOneLoses()
    {
        var productId = await SeedActiveProductAsync(stock: 3);

        await using var contextA = CreateContext();
        await using var contextB = CreateContext();

        var productA = await LoadAsync(contextA, productId);
        var productB = await LoadAsync(contextB, productId);

        // Both read version 0 and both see 3 in stock.
        Assert.Equal(0u, productA.Version);
        Assert.Equal(0u, productB.Version);
        Assert.True(productA.CanFulfil(2));
        Assert.True(productB.CanFulfil(2));

        // A commits first and advances the version to 1.
        contextA.Add(productA.Deduct(2, StockReason.Sale, "session-a", Now).Value);
        await contextA.SaveChangesAsync();

        // B's domain rule still passes against its stale in-memory value of 3 — the aggregate
        // cannot know. The database is what stops it.
        contextB.Add(productB.Deduct(2, StockReason.Sale, "session-b", Now).Value);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());

        // One sale, not two. Three units did not become four.
        await using var verify = CreateContext();
        var final = await verify.Set<Product>().SingleAsync(product => product.Id == productId);

        Assert.Equal(1, final.Stock.Quantity);
        Assert.Equal(1u, final.Version);
    }

    /// <summary>
    /// What the retry does after losing: reload, re-run the rule against the fresh value, and
    /// fail honestly. The business answer is InsufficientStock, not a 500.
    /// </summary>
    [Fact]
    public async Task AfterLosing_ReloadingReRunsTheRule_AndFailsWithABusinessError()
    {
        var productId = await SeedActiveProductAsync(stock: 3);

        await using var winner = CreateContext();
        var winning = await LoadAsync(winner, productId);
        winner.Add(winning.Deduct(2, StockReason.Sale, "session-a", Now).Value);
        await winner.SaveChangesAsync();

        // The retry: a brand new context, so a clean change tracker and a fresh read.
        await using var retry = CreateContext();
        var reloaded = await LoadAsync(retry, productId);

        var result = reloaded.Deduct(2, StockReason.Sale, "session-b", Now);

        Assert.True(result.IsError);
        Assert.Equal("StockLevel.Insufficient", result.FirstError.Code);
        Assert.Equal(1, reloaded.Stock.Quantity);
    }

    /// <summary>
    /// The version advances on any state change, not only on stock — otherwise a concurrent
    /// rename and a concurrent price change could silently overwrite each other.
    /// </summary>
    [Fact]
    public async Task ConcurrentEditsToDifferentFields_StillConflict()
    {
        var productId = await SeedActiveProductAsync(stock: 10);

        await using var contextA = CreateContext();
        await using var contextB = CreateContext();

        var productA = await LoadAsync(contextA, productId);
        var productB = await LoadAsync(contextB, productId);

        Assert.False(productA.Rename("Renamed by A", Now).IsError);
        await contextA.SaveChangesAsync();

        Assert.False(productB.Rename("Renamed by B", Now).IsError);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());

        await using var verify = CreateContext();
        var final = await verify.Set<Product>().SingleAsync(product => product.Id == productId);

        Assert.Equal("Renamed by A", final.Name);
    }

    /// <summary>
    /// SetPrice adds a CHILD entity and changes no scalar on the root, which is a different
    /// change-detection path from a stock deduction. The second save mimics what the domain
    /// event dispatcher does after publishing.
    /// </summary>
    [Fact]
    public async Task SetPrice_OnAnExistingProduct_DoesNotConflict()
    {
        var productId = await SeedActiveProductAsync(stock: 10);

        await using var context = CreateContext();
        var product = await LoadAsync(context, productId);

        var later = Now.AddDays(1);
        Assert.False(product.SetPrice(Country.UnitedArabEmirates, Money.Create(349m, "AED").Value, later, later).IsError);

        await context.SaveChangesAsync();
        await context.SaveChangesAsync();

        Assert.Equal(1u, product.Version);
        Assert.Equal(2, product.Prices.Count);
    }

    // ----------------------------------------------------------------- helpers

    private static Task<Product> LoadAsync(CatalogDbContext context, Guid productId) =>
        context.Set<Product>()
            .Include(product => product.Prices)
            .SingleAsync(product => product.Id == productId);

    private async Task<Guid> SeedActiveProductAsync(int stock)
    {
        await using var context = CreateContext();

        var product = Product.Create(
            Sku.Create($"ONX-{Guid.NewGuid():N}"[..10]).Value,
            "Test Product",
            StockLevel.Create(stock).Value,
            Now);

        product.SetPrice(Country.UnitedStates, Money.Create(89.99m, "USD").Value, Now, Now);
        product.Activate(Now);

        context.Add(product);
        await context.SaveChangesAsync();

        return product.Id;
    }
}
