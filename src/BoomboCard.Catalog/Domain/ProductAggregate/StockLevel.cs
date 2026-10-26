using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Catalog.Domain.ProductAggregate;

/// <summary>
/// An on-hand quantity that cannot be negative by construction, plus the single definition of
/// what "enough stock" means.
///
/// Onyx had three competing answers to that question: <c>available &lt; requested</c> in the
/// order path, <c>available &lt;= requested</c> in the admin bulk check (M18 — a product with
/// exactly enough stock was reported short, so staff raised supplier orders they did not
/// need), and <c>&lt; 5</c> in the replenishment job. There is one rule here and every caller
/// uses it.
/// </summary>
public sealed class StockLevel : ValueObject
{
    public int Quantity { get; }

    private StockLevel(int quantity) => Quantity = quantity;

    public static StockLevel None => new(0);

    public static ErrorOr<StockLevel> Create(int quantity) =>
        quantity < 0 ? StockLevelErrors.Negative : new StockLevel(quantity);

    /// <summary>The one availability rule in the system.</summary>
    public bool CanFulfil(int requested) => requested > 0 && Quantity >= requested;

    public ErrorOr<StockLevel> Deduct(int quantity)
    {
        if (quantity <= 0)
        {
            return StockLevelErrors.QuantityMustBePositive;
        }

        return CanFulfil(quantity)
            ? new StockLevel(Quantity - quantity)
            : StockLevelErrors.Insufficient(Quantity, quantity);
    }

    public ErrorOr<StockLevel> Add(int quantity) =>
        quantity <= 0
            ? StockLevelErrors.QuantityMustBePositive
            : new StockLevel(Quantity + quantity);

    /// <summary>
    /// Replaces the quantity outright. Used only by a stocktake, where the counted figure is
    /// the authority and the difference from the recorded figure is what gets audited.
    /// </summary>
    public ErrorOr<StockLevel> SetTo(int quantity) => Create(quantity);

    public bool IsBelow(int threshold) => Quantity < threshold;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Quantity;
    }

    public override string ToString() => Quantity.ToString();
}

public static class StockLevelErrors
{
    public static Error Negative =>
        Error.Validation("StockLevel.Negative", "Stock quantity cannot be negative.");

    public static Error QuantityMustBePositive =>
        Error.Validation("StockLevel.QuantityMustBePositive", "Quantity must be greater than zero.");

    public static Error Insufficient(int available, int requested) =>
        Error.Conflict(
            "StockLevel.Insufficient",
            $"Insufficient stock: {available} available, {requested} requested.");
}
