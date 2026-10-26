using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Tests;

/// <summary>
/// Proves the EF model is valid and round-trips against the real provider.
///
/// Note the per-test SKU prefixes. The class fixture shares ONE database across every test in
/// this class, so tests must not share identifiers — exactly the discipline Onyx T2 lacked, where
/// a shared mutable static counter made two tests pass only in one specific order and fail when
/// run individually. A successful compile says nothing about
/// whether the owned types, converters and indexes actually map — that is a runtime check.
/// </summary>
public sealed class CatalogModelTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private CatalogDbContext CreateContext() => fixture.CreateContext();

    [Fact]
    public async Task Model_IsValid_AndSchemaCanBeCreated()
    {
        await using var context = CreateContext();

        // Throws if any owned type, value converter or index is misconfigured.
        var model = context.Model;
        Assert.NotNull(model.FindEntityType(typeof(Product)));
        Assert.NotNull(model.FindEntityType(typeof(ProductPrice)));
        Assert.NotNull(model.FindEntityType(typeof(StockMovement)));

    }

    [Fact]
    public async Task Product_RoundTrips_WithPricesAndValueObjects()
    {
        await using var write = CreateContext();

        var nowUtc = DateTimeOffset.Parse("2026-10-23T09:00:00Z");
        var product = NewProduct("RT-001", 40, nowUtc);

        var usd = Money.Create(89.99m, "USD").Value;
        var aed = Money.Create(349.00m, "AED").Value;

        Assert.False(product.SetPrice(Country.UnitedStates, usd, nowUtc, nowUtc).IsError);
        Assert.False(product.SetPrice(Country.UnitedArabEmirates, aed, nowUtc, nowUtc).IsError);
        Assert.False(product.Activate(nowUtc).IsError);

        write.Add(product);
        await write.SaveChangesAsync();

        await using var read = CreateContext();
        var loaded = await read.Set<Product>()
            .Include(candidate => candidate.Prices)
            .SingleAsync(candidate => candidate.Id == product.Id);

        Assert.Equal("RT-001", loaded.Sku.Value);
        Assert.Equal(40, loaded.Stock.Quantity);
        Assert.True(loaded.Status.IsActive);
        Assert.Equal(2, loaded.Prices.Count);

        // Explicit price per country, each in that country's own currency.
        Assert.Equal(89.99m, loaded.PriceFor("US", nowUtc)!.Amount);
        Assert.Equal("USD", loaded.PriceFor("US", nowUtc)!.CurrencyCode);
        Assert.Equal(349.00m, loaded.PriceFor("AE", nowUtc)!.Amount);
        Assert.Equal("AED", loaded.PriceFor("AE", nowUtc)!.CurrencyCode);
    }

    /// <summary>Onyx D10: nothing enforced SKU uniqueness, so CSV import duplicated freely.</summary>
    [Fact]
    public async Task DuplicateSku_IsRejectedByTheDatabase()
    {
        await using var context = CreateContext();

        var nowUtc = DateTimeOffset.Parse("2026-10-23T09:00:00Z");

        context.Add(NewProduct("DUP-001", 10, nowUtc));
        await context.SaveChangesAsync();

        // Same SKU typed differently — Sku normalises to upper case, so this collides.
        context.Add(NewProduct("dup-001", 5, nowUtc));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    /// <summary>
    /// Onyx M5: three payment attempts with no idempotency key, so a timeout plus retry could
    /// charge twice. The same index is what makes an at-least-once stock restore safe.
    /// </summary>
    [Fact]
    public async Task DuplicateMovementForSameReferenceAndReason_IsRejectedByTheDatabase()
    {
        await using var context = CreateContext();

        var nowUtc = DateTimeOffset.Parse("2026-10-23T09:00:00Z");
        var product = NewProduct("IDEM-001", 10, nowUtc);
        product.SetPrice(Country.UnitedStates, Money.Create(29.99m, "USD").Value, nowUtc, nowUtc);
        product.Activate(nowUtc);

        context.Add(product);
        await context.SaveChangesAsync();

        context.Add(product.Deduct(1, StockReason.Sale, "session-1", nowUtc).Value);
        await context.SaveChangesAsync();

        context.Add(product.Deduct(1, StockReason.Sale, "session-1", nowUtc).Value);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    /// <summary>
    /// The reconciliation invariant Onyx never had: its replenishment job added 50 units from
    /// nowhere every 15 minutes (D5), which silently healed every oversell (D1).
    /// </summary>
    [Fact]
    public async Task SumOfMovements_EqualsCurrentQuantity()
    {
        await using var context = CreateContext();

        var nowUtc = DateTimeOffset.Parse("2026-10-23T09:00:00Z");
        var product = NewProduct("SUM-001", 0, nowUtc);
        product.SetPrice(Country.UnitedStates, Money.Create(249.00m, "USD").Value, nowUtc, nowUtc);
        product.Activate(nowUtc);
        context.Add(product);

        context.Add(product.Restore(15, StockReason.Receipt, "PO-100", nowUtc).Value);
        context.Add(product.Deduct(4, StockReason.Sale, "session-1", nowUtc).Value);
        context.Add(product.Restore(4, StockReason.PaymentFailed, "session-1", nowUtc).Value);
        context.Add(product.Deduct(3, StockReason.Sale, "session-2", nowUtc).Value);
        await context.SaveChangesAsync();

        var sum = await context.Set<StockMovement>()
            .Where(movement => movement.ProductId == product.Id)
            .SumAsync(movement => movement.Delta);

        Assert.Equal(12, product.Stock.Quantity);
        Assert.Equal(product.Stock.Quantity, sum);
    }

    private static Product NewProduct(string sku, int stock, DateTimeOffset nowUtc) =>
        Product.Create(
            Sku.Create(sku).Value,
            $"Product {sku}",
            StockLevel.Create(stock).Value,
            nowUtc);
}
