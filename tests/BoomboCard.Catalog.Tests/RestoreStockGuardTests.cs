using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Tests;

/// <summary>
/// <b>The regression tests for BUGS-AND-LESSONS section 13: stock restored that was never
/// deducted.</b>
///
/// <para>
/// Found by the Phase 4 end-to-end run. A customer abandoned an <c>Open</c> checkout, Sales'
/// release handler read the session's <i>lines</i> and asked Catalog to restore them, and
/// Catalog did — adding two units to stock that had never left it. Onyx <b>D5</b>'s failure mode
/// (a job inventing inventory) arriving in our own code by a different road.
/// </para>
///
/// <para>
/// Idempotency did not catch it and could not have. The unique index on
/// <c>(ProductId, Reference, Reason)</c> stops an operation being <i>repeated</i>; this was an
/// operation being performed <i>wrongly</i> the first time, and it was the first of its kind for
/// that reference, so the insert was perfectly legal.
/// </para>
///
/// <para>
/// There are two fixes and both are here: Sales no longer asks (it records whether TX 1 ever
/// ran), and Catalog no longer complies (it clamps every release to the net still held under
/// that reference). These test Catalog's half, because that is the one that makes the whole
/// class of bug impossible rather than fixing the one instance of it.
/// </para>
/// </summary>
public sealed class RestoreStockGuardTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T09:00:00Z");

    /// <summary>
    /// <b>The bug, as a test.</b> A reference that never deducted anything holds nothing, so
    /// there is nothing to give back.
    /// </summary>
    [Fact]
    public async Task AReferenceThatNeverDeductedAnythingHoldsNothing()
    {
        var product = await SeedAsync("GUARD-NEVER", stock: 10);

        await using var context = fixture.CreateContext();
        var repository = new ProductRepository(context);

        var held = await repository.GetHeldQuantitiesAsync(
            "a-session-that-never-confirmed", [product]);

        // Absent, not zero: the caller reads a missing entry as "nothing is held", and an entry
        // of 0 would be indistinguishable from a product that was deducted and fully released.
        Assert.Empty(held);
    }

    /// <summary>A deduction leaves exactly that much held.</summary>
    [Fact]
    public async Task ADeductionLeavesThatMuchHeld()
    {
        var product = await SeedAsync("GUARD-HELD", stock: 10);
        await DeductAsync(product, 3, "session-held");

        await using var context = fixture.CreateContext();
        var held = await new ProductRepository(context)
            .GetHeldQuantitiesAsync("session-held", [product]);

        Assert.Equal(3, held[product]);
    }

    /// <summary>
    /// A release takes the held figure back to nothing, so a <i>second</i> release finds nothing
    /// to give — which is the same answer the idempotency index would have forced, reached by
    /// arithmetic rather than by an exception.
    /// </summary>
    [Fact]
    public async Task AFullReleaseLeavesNothingHeld()
    {
        var product = await SeedAsync("GUARD-RELEASED", stock: 10);
        await DeductAsync(product, 4, "session-released");
        await RestoreAsync(product, 4, StockReason.SessionExpired, "session-released");

        await using var context = fixture.CreateContext();
        var held = await new ProductRepository(context)
            .GetHeldQuantitiesAsync("session-released", [product]);

        Assert.Empty(held);
    }

    /// <summary>
    /// <b>Reasons outside the checkout lifecycle do not net against a sale.</b>
    ///
    /// <para>
    /// A stocktake correction references a user and a goods-in references a delivery note, so
    /// letting either cancel out a sale would make a real hold look settled — and the orphan
    /// sweep would then stop looking for it. The two queries that compute "still held" share
    /// this reason list precisely so the restore cap and the sweep cannot disagree.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnAdjustmentDoesNotNetAgainstASale()
    {
        var product = await SeedAsync("GUARD-REASONS", stock: 10);
        await DeductAsync(product, 5, "session-reasons");

        // Same reference, deliberately — a contrived collision, because the point is that the
        // REASON is what excludes it, not the reference being different.
        await RestoreAsync(product, 5, StockReason.Receipt, "session-reasons");

        await using var context = fixture.CreateContext();
        var held = await new ProductRepository(context)
            .GetHeldQuantitiesAsync("session-reasons", [product]);

        Assert.Equal(5, held[product]);
    }

    /// <summary>
    /// The sum is per product, so one basket's two products are tracked separately and a
    /// release for one does not appear to settle the other.
    /// </summary>
    [Fact]
    public async Task HeldQuantitiesArePerProduct()
    {
        var first = await SeedAsync("GUARD-MULTI-A", stock: 10);
        var second = await SeedAsync("GUARD-MULTI-B", stock: 10);

        await DeductAsync(first, 2, "session-multi");
        await DeductAsync(second, 7, "session-multi");
        await RestoreAsync(first, 2, StockReason.SessionExpired, "session-multi");

        await using var context = fixture.CreateContext();
        var held = await new ProductRepository(context)
            .GetHeldQuantitiesAsync("session-multi", [first, second]);

        Assert.False(held.ContainsKey(first));
        Assert.Equal(7, held[second]);
    }

    /// <summary>
    /// A deduct-and-release cycle leaves the counter where it started and the movements netting
    /// to zero — the two halves of the invariant <c>catalog.stock-integrity-check</c> asserts,
    /// and the pair a release that invented stock breaks while the counter alone still looks
    /// perfectly plausible.
    ///
    /// <para>
    /// The assertion is on the <i>delta</i> rather than on <c>SUM(Delta) == Stock</c>, because
    /// these products are seeded through the domain directly and so have no
    /// <c>OPENING-BALANCE</c> movement — <c>CreateProductCommandHandler</c> writes that, and
    /// <c>CatalogModelTests</c> covers the absolute form. Here the movements are exactly the
    /// cycle under test, which is what makes a net of zero meaningful.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADeductAndReleaseCycleLeavesTheCounterWhereItStarted()
    {
        var product = await SeedAsync("GUARD-INTEGRITY", stock: 10);
        await DeductAsync(product, 3, "session-integrity");
        await RestoreAsync(product, 3, StockReason.SessionExpired, "session-integrity");

        await using var context = fixture.CreateContext();

        var reloaded = await context.Products.FirstAsync(candidate => candidate.Id == product);

        var net = await context.StockMovements
            .Where(movement => movement.ProductId == product)
            .SumAsync(movement => movement.Delta);

        Assert.Equal(10, reloaded.Stock.Quantity);
        Assert.Equal(0, net);
    }

    /// <summary>
    /// And the shape of the bug, stated as the arithmetic that caught it: a release with no
    /// matching deduction nets <i>positive</i>, which is stock that came from nowhere.
    ///
    /// <para>
    /// Asserted against the domain rather than the handler, because the domain will happily do
    /// it — <c>Product.Restore</c> knows nothing about references. The guard lives in the
    /// handler, which is the layer that can see the log.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARestoreWithNoMatchingDeductionWouldNetPositive()
    {
        var product = await SeedAsync("GUARD-INVENTED", stock: 10);
        await RestoreAsync(product, 2, StockReason.SessionExpired, "never-deducted-anything");

        await using var context = fixture.CreateContext();

        var net = await context.StockMovements
            .Where(movement => movement.ProductId == product)
            .SumAsync(movement => movement.Delta);

        // Two units from nowhere. This is what the end-to-end run found, and it is why the
        // handler asks the log how much is held before restoring anything.
        Assert.Equal(2, net);

        var held = await new ProductRepository(context)
            .GetHeldQuantitiesAsync("never-deducted-anything", [product]);

        Assert.Empty(held);
    }

    private async Task<Guid> SeedAsync(string sku, int stock)
    {
        await using var context = fixture.CreateContext();

        var product = Product.Create(
            Sku.Create($"{sku}-{Guid.NewGuid():N}"[..20]).Value,
            $"Product {sku}",
            StockLevel.Create(stock).Value,
            Now);

        // Priced and activated, because Deduct refuses a Draft — a product with no price cannot
        // be sold, so it cannot have stock taken off the shelf for a sale either.
        product.SetPrice(Country.UnitedStates, Money.Create(19.99m, "USD").Value, Now, Now);
        product.Activate(Now);

        context.Products.Add(product);
        await context.SaveChangesAsync();

        return product.Id;
    }

    private async Task DeductAsync(Guid productId, int quantity, string reference)
    {
        await using var context = fixture.CreateContext();
        var product = await context.Products.FirstAsync(candidate => candidate.Id == productId);

        context.Add(product.Deduct(quantity, StockReason.Sale, reference, Now).Value);
        await context.SaveChangesAsync();
    }

    private async Task RestoreAsync(
        Guid productId,
        int quantity,
        StockReason reason,
        string reference)
    {
        await using var context = fixture.CreateContext();
        var product = await context.Products.FirstAsync(candidate => candidate.Id == productId);

        context.Add(product.Restore(quantity, reason, reference, Now).Value);
        await context.SaveChangesAsync();
    }
}
