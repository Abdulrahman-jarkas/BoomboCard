using System.Text.RegularExpressions;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Catalog.Domain.ProductAggregate;

/// <summary>
/// A stock-keeping unit code. Normalised to upper case on creation so that uniqueness is a
/// property of the value, not of how someone typed it.
///
/// Onyx D10: nothing enforced uniqueness of Product.Sku, and its CSV import always inserted,
/// so re-importing an edited sheet duplicated the whole catalogue. Normalising here plus the
/// unique index in ProductConfiguration closes both halves.
/// </summary>
public sealed partial class Sku : ValueObject
{
    public const int MaxLength = 32;

    public string Value { get; }

    private Sku(string value) => Value = value;

    public static ErrorOr<Sku> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SkuErrors.Required;
        }

        var normalised = value.Trim().ToUpperInvariant();

        if (normalised.Length > MaxLength)
        {
            return SkuErrors.TooLong;
        }

        if (!AllowedPattern().IsMatch(normalised))
        {
            return SkuErrors.InvalidFormat;
        }

        return new Sku(normalised);
    }

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9\-_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPattern();

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}

public static class SkuErrors
{
    public static Error Required =>
        Error.Validation("Sku.Required", "A SKU is required.");

    public static Error TooLong =>
        Error.Validation("Sku.TooLong", $"A SKU may be at most {Sku.MaxLength} characters.");

    public static Error InvalidFormat =>
        Error.Validation(
            "Sku.InvalidFormat",
            "A SKU may contain only letters, digits, hyphens and underscores, and must start "
            + "with a letter or digit.");
}
