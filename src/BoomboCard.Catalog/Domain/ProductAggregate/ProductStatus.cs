using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Catalog.Domain.ProductAggregate;

public enum ProductStatusValue
{
    Draft = 0,
    Active = 1,
    Discontinued = 2
}

/// <summary>
/// The product lifecycle, with the legal transitions held in the value object rather than in
/// scattered if-statements.
///
/// Onyx had no lifecycle at all: no delete, no discontinued flag, no way to withdraw anything.
/// Once a product existed it stayed purchasable forever, and the replenishment job kept
/// topping it up every 15 minutes (A31, D5).
///
/// Draft exists so a product can be created and priced before it is sellable — Product.Activate
/// refuses without an effective price, which is the invariant that stops Onyx's "charged a
/// price nobody had set" class of bug.
/// </summary>
public sealed class ProductStatus : ValueObject
{
    public ProductStatusValue Value { get; }

    private ProductStatus(ProductStatusValue value) => Value = value;

    public static ProductStatus Draft => new(ProductStatusValue.Draft);

    public static ProductStatus From(ProductStatusValue value) => new(value);

    public bool IsDraft => Value is ProductStatusValue.Draft;

    public bool IsActive => Value is ProductStatusValue.Active;

    public bool IsDiscontinued => Value is ProductStatusValue.Discontinued;

    public ErrorOr<ProductStatus> Activate() =>
        Value switch
        {
            ProductStatusValue.Active => ProductStatusErrors.AlreadyActive,
            _ => new ProductStatus(ProductStatusValue.Active)
        };

    public ErrorOr<ProductStatus> Discontinue() =>
        Value switch
        {
            ProductStatusValue.Discontinued => ProductStatusErrors.AlreadyDiscontinued,
            _ => new ProductStatus(ProductStatusValue.Discontinued)
        };

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString();
}

public static class ProductStatusErrors
{
    public static Error AlreadyActive =>
        Error.Conflict("ProductStatus.AlreadyActive", "The product is already active.");

    public static Error AlreadyDiscontinued =>
        Error.Conflict("ProductStatus.AlreadyDiscontinued", "The product is already discontinued.");
}
