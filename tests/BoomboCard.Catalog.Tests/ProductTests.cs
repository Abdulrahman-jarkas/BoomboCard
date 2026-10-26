using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.ProductAggregate.Events;
using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel;

namespace BoomboCard.Catalog.Tests;

public sealed class ProductTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T09:00:00Z");

    // ------------------------------------------------------------------ domain events

    [Fact]
    public void Create_RaisesProductCreated()
    {
        var product = NewProduct(stock: 10);

        var created = Assert.Single(product.DomainEvents.OfType<ProductCreatedEvent>());
        Assert.Equal("ONX-001", created.Sku);
        Assert.Equal(10, created.InitialQuantity);
    }

    /// <summary>
    /// Nothing subscribes to ProductRenamedEvent today. It is raised anyway: the event is the
    /// record of the fact, and adding a listener later must not require editing the aggregate.
    /// </summary>
    [Fact]
    public void Rename_RaisesProductRenamed_EvenWithNoSubscribers()
    {
        var product = NewProduct();
        product.PopEventsForTest();

        Assert.False(product.Rename("Mechanical Keyboard", Now).IsError);

        var renamed = Assert.Single(product.DomainEvents.OfType<ProductRenamedEvent>());
        Assert.Equal("Product ONX-001", renamed.PreviousName);
        Assert.Equal("Mechanical Keyboard", renamed.NewName);
    }

    [Fact]
    public void Rename_ToTheSameName_RaisesNothing()
    {
        var product = NewProduct();
        product.PopEventsForTest();

        Assert.False(product.Rename("Product ONX-001", Now).IsError);

        Assert.Empty(product.DomainEvents);
    }

    /// <summary>
    /// Carries both price ids so a consumer can honour an existing quote or re-quote, rather
    /// than cancelling every session touching the product the way Talabat's handler does.
    /// </summary>
    [Fact]
    public void SetPrice_Twice_SupersedesAndCarriesBothPriceIds()
    {
        var product = NewProduct();
        Assert.False(product.SetPrice(Country.UnitedStates, Usd(89.99m), Now, Now).IsError);

        var firstPriceId = product.DomainEvents.OfType<ProductPriceChangedEvent>().Single().NewPriceId;
        product.PopEventsForTest();

        var later = Now.AddDays(1);
        Assert.False(product.SetPrice(Country.UnitedStates, Usd(99.99m), later, later).IsError);

        var changed = Assert.Single(product.DomainEvents.OfType<ProductPriceChangedEvent>());
        Assert.Equal(firstPriceId, changed.PreviousPriceId);
        Assert.Equal(89.99m, changed.PreviousAmount);
        Assert.Equal(99.99m, changed.NewAmount);
        Assert.NotEqual(firstPriceId, changed.NewPriceId);

        // The old price survives as a historical fact that invoices still reference.
        Assert.Equal(2, product.Prices.Count);
        Assert.Equal(89.99m, product.PriceFor("US", Now)!.Amount);
        Assert.Equal(99.99m, product.PriceFor("US", later)!.Amount);
    }

    // ------------------------------------------------------------------ invariants

    [Fact]
    public void SetPrice_InTheWrongCurrencyForTheCountry_IsRejected()
    {
        var product = NewProduct();

        var result = product.SetPrice(Country.UnitedArabEmirates, Usd(89.99m), Now, Now);

        Assert.True(result.IsError);
        Assert.Equal("Product.PriceCurrencyMismatch", result.FirstError.Code);
    }

    /// <summary>
    /// The invariant behind "the price you see is the price you pay": a product with no price
    /// can never reach a basket.
    /// </summary>
    [Fact]
    public void Activate_WithoutAPrice_IsRejected()
    {
        var product = NewProduct();

        var result = product.Activate(Now);

        Assert.True(result.IsError);
        Assert.Equal("Product.CannotActivateWithoutPrice", result.FirstError.Code);
        Assert.True(product.Status.IsDraft);
    }

    [Fact]
    public void Deduct_FromADraftProduct_IsRejected()
    {
        var product = NewProduct(stock: 10);

        var result = product.Deduct(1, StockReason.Sale, "session-1", Now);

        Assert.True(result.IsError);
        Assert.Equal("Product.NotActive", result.FirstError.Code);
        Assert.Equal(10, product.Stock.Quantity);
    }

    [Fact]
    public void Deduct_WithoutAReference_IsRejected()
    {
        var product = ActiveProduct(stock: 10);

        var result = product.Deduct(1, StockReason.Sale, "  ", Now);

        Assert.True(result.IsError);
        Assert.Equal("Product.ReferenceRequired", result.FirstError.Code);
    }

    [Fact]
    public void WithdrawPrice_WhenItIsTheLastOneOnAnActiveProduct_IsRejected()
    {
        var product = ActiveProduct();

        var result = product.WithdrawPrice(Country.UnitedStates, Now);

        Assert.True(result.IsError);
        Assert.Equal("Product.CannotWithdrawLastPrice", result.FirstError.Code);
    }

    // ------------------------------------------------------------------ stock

    /// <summary>
    /// The boundary Onyx got wrong. StockPolicy.IsInsufficient used
    /// <c>available &lt;= requested</c>, so a product with exactly enough stock was reported
    /// short and staff raised supplier orders they did not need (M18).
    /// </summary>
    [Fact]
    public void Deduct_ExactlyAllRemainingStock_Succeeds()
    {
        var product = ActiveProduct(stock: 3);

        var movement = product.Deduct(3, StockReason.Sale, "session-1", Now);

        Assert.False(movement.IsError);
        Assert.Equal(0, product.Stock.Quantity);
        Assert.Equal(-3, movement.Value.Delta);
        Assert.Equal(0, movement.Value.QuantityAfter);
    }

    [Fact]
    public void Deduct_OneMoreThanAvailable_IsRejectedAndLeavesStockUntouched()
    {
        var product = ActiveProduct(stock: 3);

        var result = product.Deduct(4, StockReason.Sale, "session-1", Now);

        Assert.True(result.IsError);
        Assert.Equal("StockLevel.Insufficient", result.FirstError.Code);
        Assert.Equal(3, product.Stock.Quantity);
        Assert.Empty(product.DomainEvents.OfType<StockDeductedEvent>());
    }

    [Fact]
    public void Deduct_ThenRestore_ReturnsToTheOriginalQuantity()
    {
        var product = ActiveProduct(stock: 10);

        product.Deduct(4, StockReason.Sale, "session-1", Now);
        var restored = product.Restore(4, StockReason.PaymentFailed, "session-1", Now);

        Assert.False(restored.IsError);
        Assert.Equal(10, product.Stock.Quantity);
        Assert.Equal(StockReason.PaymentFailed, restored.Value.Reason);
        Assert.Equal("session-1", restored.Value.Reference);
    }

    /// <summary>Restores must work on a withdrawn product so returns can still reconcile.</summary>
    [Fact]
    public void Restore_OnADiscontinuedProduct_IsAllowed()
    {
        var product = ActiveProduct(stock: 5);
        Assert.False(product.Discontinue(Now).IsError);

        var restored = product.Restore(2, StockReason.Return, "order-9", Now);

        Assert.False(restored.IsError);
        Assert.Equal(7, product.Stock.Quantity);
    }

    [Fact]
    public void Deduct_CrossingTheLowStockThreshold_RaisesTheThresholdEventOnce()
    {
        var product = ActiveProduct(stock: 6);   // threshold is 5

        product.Deduct(2, StockReason.Sale, "session-1", Now);   // 6 -> 4, crosses
        var afterCrossing = product.PopEventsForTest();

        product.Deduct(1, StockReason.Sale, "session-2", Now);   // 4 -> 3, already below
        var afterSecond = product.PopEventsForTest();

        Assert.Single(afterCrossing.OfType<StockFellBelowThresholdEvent>());
        Assert.Empty(afterSecond.OfType<StockFellBelowThresholdEvent>());
    }

    // ------------------------------------------------------------------ helpers

    private static Money Usd(decimal amount) => Money.Create(amount, "USD").Value;

    private static Product NewProduct(int stock = 0) =>
        Product.Create(
            Sku.Create("ONX-001").Value,
            "Product ONX-001",
            StockLevel.Create(stock).Value,
            Now);

    private static Product ActiveProduct(int stock = 0)
    {
        var product = NewProduct(stock);
        product.SetPrice(Country.UnitedStates, Usd(89.99m), Now, Now);
        product.Activate(Now);
        product.PopEventsForTest();
        return product;
    }
}

internal static class ProductTestExtensions
{
    /// <summary>
    /// Snapshots and clears pending events so a test can assert on one operation at a time.
    /// Uses the same seam the persistence layer uses, rather than exposing a clear method on
    /// the aggregate itself.
    /// </summary>
    public static IReadOnlyList<IDomainEvent> PopEventsForTest(this Product product) =>
        DomainEventAccess.Drain(product);
}
