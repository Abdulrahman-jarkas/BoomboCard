using BoomboCard.Catalog.Domain.ProductAggregate.Events;
using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Catalog.Domain.ProductAggregate;

/// <summary>
/// A sellable catalogue item: identity, lifecycle, an explicit price per country, and an on-hand
/// quantity.
///
/// No category, deliberately — categories are on the capability-gap list rather than built, and
/// this comment claimed one for a while without the property existing. See CONCEPTS.md §11.
///
/// Invariants it holds:
///  - Stock never goes negative (StockLevel cannot represent a negative quantity).
///  - A product cannot be activated without an effective price in at least one country.
///  - Stock can only be deducted from an Active product.
///  - A price's currency must match its country's currency.
///  - At most one open price per country at a time; replacing one supersedes the old.
///
/// Mutating methods return the <see cref="StockMovement"/> they imply rather than writing it
/// themselves, so the caller cannot produce a stock change with no audit row — and so the
/// aggregate stays free of persistence concerns. dome-gym smuggles a second aggregate out
/// through a domain event handler to get it saved, which hides the fact that the command writes
/// twice.
/// </summary>
public sealed class Product : AggregateRoot
{
    public const int DefaultLowStockThreshold = 5;
    public const int MaxNameLength = 200;

    private readonly List<ProductPrice> _prices = [];

    public Sku Sku { get; private set; } = null!;

    public string Name { get; private set; } = string.Empty;

    public ProductStatus Status { get; private set; } = ProductStatus.Draft;

    public StockLevel Stock { get; private set; } = StockLevel.None;

    /// <summary>
    /// What the product costs us. Nullable because it is not always known at creation.
    /// Onyx had no cost price at all, so margin was unknowable — and its 5% volume discount
    /// and promo codes were therefore handed out with no visibility of whether they were
    /// profitable.
    /// </summary>
    public Money? CostPrice { get; private set; }

    public int LowStockThreshold { get; private set; } = DefaultLowStockThreshold;

    public IReadOnlyCollection<ProductPrice> Prices => _prices.AsReadOnly();

    private Product() { }

    public static Product Create(
        Sku sku,
        string name,
        StockLevel initialStock,
        DateTimeOffset nowUtc,
        int lowStockThreshold = DefaultLowStockThreshold)
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Sku = sku,
            Name = name.Trim(),
            Status = ProductStatus.Draft,
            Stock = initialStock,
            LowStockThreshold = lowStockThreshold
        };

        product.RaiseDomainEvent(new ProductCreatedEvent(
            product.Id,
            sku.Value,
            product.Name,
            initialStock.Quantity,
            nowUtc));

        return product;
    }

    // ---------------------------------------------------------------- catalogue details

    public ErrorOr<Success> Rename(string name, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProductErrors.NameRequired;
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            return ProductErrors.NameTooLong;
        }

        if (trimmed == Name)
        {
            return Result.Success;
        }

        var previous = Name;
        Name = trimmed;

        RaiseDomainEvent(new ProductRenamedEvent(Id, previous, trimmed, nowUtc));

        return Result.Success;
    }


    public ErrorOr<Success> ChangeCostPrice(Money costPrice, DateTimeOffset nowUtc)
    {
        if (CostPrice == costPrice)
        {
            return Result.Success;
        }

        var previous = CostPrice;
        CostPrice = costPrice;

        RaiseDomainEvent(new ProductCostPriceChangedEvent(
            Id,
            previous?.Amount,
            costPrice.Amount,
            costPrice.CurrencyCode,
            nowUtc));

        return Result.Success;
    }

    // ---------------------------------------------------------------- pricing

    /// <summary>
    /// Sets the price for one country from <paramref name="effectiveFrom"/>, superseding the
    /// open price rather than overwriting it, so the previous price survives as a fact that
    /// historical quotes and invoices still reference.
    /// </summary>
    public ErrorOr<Success> SetPrice(
        Country country,
        Money amount,
        DateTimeOffset effectiveFrom,
        DateTimeOffset nowUtc)
    {
        if (amount.CurrencyCode != country.CurrencyCode)
        {
            return ProductErrors.PriceCurrencyMismatch(country.Code, country.CurrencyCode, amount.CurrencyCode);
        }

        if (amount.IsZero)
        {
            return ProductErrors.PriceMustBePositive;
        }

        var existing = OpenPriceFor(country.Code);

        if (existing is not null)
        {
            if (existing.Amount == amount)
            {
                return Result.Success;
            }

            if (effectiveFrom <= existing.EffectiveFrom)
            {
                return ProductErrors.PriceNotAfterCurrent(country.Code);
            }

            existing.SupersedeAt(effectiveFrom);
        }

        var price = ProductPrice.Create(Id, country, amount, effectiveFrom);
        _prices.Add(price);

        RaiseDomainEvent(new ProductPriceChangedEvent(
            Id,
            country.Code,
            existing?.Id,
            existing?.Amount.Amount,
            price.Id,
            amount.Amount,
            amount.CurrencyCode,
            effectiveFrom,
            nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> WithdrawPrice(Country country, DateTimeOffset nowUtc)
    {
        var existing = OpenPriceFor(country.Code);

        if (existing is null)
        {
            return ProductErrors.NoPriceForCountry(country.Code);
        }

        // Withdrawing the last price would leave an active product unsellable at a price
        // nobody set, which is the state Onyx charged customers from.
        if (Status.IsActive && _prices.Count(price => price.IsCurrentlyOpen) == 1)
        {
            return ProductErrors.CannotWithdrawLastPrice;
        }

        existing.SupersedeAt(nowUtc);

        RaiseDomainEvent(new ProductPriceWithdrawnEvent(Id, country.Code, existing.Id, nowUtc));

        return Result.Success;
    }

    public Money? PriceFor(string countryCode, DateTimeOffset at) =>
        _prices.SingleOrDefault(price =>
            price.CountryCode == countryCode && price.IsEffectiveAt(at))?.Amount;

    public bool HasPriceFor(string countryCode, DateTimeOffset at) =>
        PriceFor(countryCode, at) is not null;

    private ProductPrice? OpenPriceFor(string countryCode) =>
        _prices.SingleOrDefault(price => price.CountryCode == countryCode && price.IsCurrentlyOpen);

    // ---------------------------------------------------------------- lifecycle

    public ErrorOr<Success> Activate(DateTimeOffset nowUtc)
    {
        // The invariant that makes "the price you see is the price you pay" enforceable: a
        // product with no price can never reach a basket.
        if (!_prices.Any(price => price.IsEffectiveAt(nowUtc) || price.EffectiveFrom > nowUtc))
        {
            return ProductErrors.CannotActivateWithoutPrice;
        }

        var status = Status.Activate();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        RaiseDomainEvent(new ProductActivatedEvent(Id, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> Discontinue(DateTimeOffset nowUtc)
    {
        var status = Status.Discontinue();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        RaiseDomainEvent(new ProductDiscontinuedEvent(Id, nowUtc));

        return Result.Success;
    }

    // ---------------------------------------------------------------- stock

    /// <summary>
    /// Takes stock off the shelf and returns the movement that records it.
    ///
    /// Concurrency is handled by the Version token, not by a lock: EF writes
    /// <c>WHERE Id = @id AND Version = @original</c>, so a losing writer affects zero rows and
    /// the command is retried against fresh state. Onyx instead used a process-local
    /// SemaphoreSlim (D16) which protected one instance, and leaked permanently on throw
    /// because Release() sat after two possible exits with no try/finally.
    /// </summary>
    public ErrorOr<StockMovement> Deduct(
        int quantity,
        StockReason reason,
        string reference,
        DateTimeOffset nowUtc)
    {
        if (!Status.IsActive)
        {
            return ProductErrors.NotActive(Id);
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            return ProductErrors.ReferenceRequired;
        }

        var wasAtOrAboveThreshold = !Stock.IsBelow(LowStockThreshold);

        var deducted = Stock.Deduct(quantity);

        if (deducted.IsError)
        {
            return deducted.Errors;
        }

        Stock = deducted.Value;

        RaiseDomainEvent(new StockDeductedEvent(
            Id, quantity, reason, reference, Stock.Quantity, nowUtc));

        RaiseThresholdEventIfCrossed(wasAtOrAboveThreshold, nowUtc);

        return StockMovement.Create(Id, -quantity, reason, reference, Stock.Quantity, nowUtc);
    }

    /// <summary>
    /// Puts stock back — payment failed, the session expired, the order was cancelled, goods
    /// were returned or received.
    ///
    /// Deliberately allowed on a Discontinued product: a return or a correction must still
    /// reconcile. Only <see cref="Deduct"/> requires Active.
    /// </summary>
    public ErrorOr<StockMovement> Restore(
        int quantity,
        StockReason reason,
        string reference,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return ProductErrors.ReferenceRequired;
        }

        var restored = Stock.Add(quantity);

        if (restored.IsError)
        {
            return restored.Errors;
        }

        Stock = restored.Value;

        RaiseDomainEvent(new StockRestoredEvent(
            Id, quantity, reason, reference, Stock.Quantity, nowUtc));

        return StockMovement.Create(Id, quantity, reason, reference, Stock.Quantity, nowUtc);
    }

    /// <summary>
    /// A stocktake. The counted figure becomes the recorded figure, and the discrepancy is what
    /// gets audited. Allowed on any status, because a count is a fact about the shelf regardless
    /// of whether the product is still sellable.
    ///
    /// A matching count still produces a movement with Delta 0 — "we counted and it was right"
    /// is itself worth recording.
    /// </summary>
    public ErrorOr<StockMovement> AdjustTo(int countedQuantity, string reference, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return ProductErrors.ReferenceRequired;
        }

        var counted = Stock.SetTo(countedQuantity);

        if (counted.IsError)
        {
            return counted.Errors;
        }

        var wasAtOrAboveThreshold = !Stock.IsBelow(LowStockThreshold);
        var delta = countedQuantity - Stock.Quantity;

        Stock = counted.Value;

        RaiseDomainEvent(new StockAdjustedEvent(Id, delta, reference, Stock.Quantity, nowUtc));
        RaiseThresholdEventIfCrossed(wasAtOrAboveThreshold, nowUtc);

        return StockMovement.Create(Id, delta, StockReason.Adjustment, reference, Stock.Quantity, nowUtc);
    }

    /// <summary>
    /// Writes stock off as lost, broken or stolen.
    ///
    /// Separate from <see cref="Deduct"/> because the cause is known and no money came in, and
    /// allowed on a Discontinued product because a withdrawn product can still be damaged.
    /// </summary>
    public ErrorOr<StockMovement> RecordShrinkage(int quantity, string reference, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return ProductErrors.ReferenceRequired;
        }

        var wasAtOrAboveThreshold = !Stock.IsBelow(LowStockThreshold);

        var deducted = Stock.Deduct(quantity);

        if (deducted.IsError)
        {
            return deducted.Errors;
        }

        Stock = deducted.Value;

        RaiseDomainEvent(new StockShrinkageRecordedEvent(Id, quantity, reference, Stock.Quantity, nowUtc));
        RaiseThresholdEventIfCrossed(wasAtOrAboveThreshold, nowUtc);

        return StockMovement.Create(Id, -quantity, StockReason.Shrinkage, reference, Stock.Quantity, nowUtc);
    }

    public bool CanFulfil(int requested) => Status.IsActive && Stock.CanFulfil(requested);

    /// <summary>
    /// Fires on the crossing only, so a product that is already low does not raise this again on
    /// every subsequent movement.
    /// </summary>
    private void RaiseThresholdEventIfCrossed(bool wasAtOrAboveThreshold, DateTimeOffset nowUtc)
    {
        if (wasAtOrAboveThreshold && Stock.IsBelow(LowStockThreshold))
        {
            RaiseDomainEvent(new StockFellBelowThresholdEvent(
                Id, Sku.Value, Stock.Quantity, LowStockThreshold, nowUtc));
        }
    }
}
