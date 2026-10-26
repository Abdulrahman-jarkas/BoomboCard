using System.Text.RegularExpressions;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Promotions.Domain.PromotionAggregate;

/// <summary>
/// The code a customer types. Normalised to upper case, so uniqueness and lookup are properties
/// of the value rather than of how someone typed it.
///
/// <para>
/// Onyx held its codes in a <c>Dictionary&lt;string, DateTime&gt;</c> with
/// <c>StringComparer.OrdinalIgnoreCase</c> — which handled case correctly — and then matched the
/// discount rate with <c>promoCode.ToUpperInvariant() switch { "WELCOME10" => 0.10m, ... }</c>,
/// so the <i>expiry</i> lookup and the <i>rate</i> lookup were two separate pieces of code
/// keyed two different ways on the same string. That is the shape of <b>M14</b>: not one wrong
/// value, but two places that both had to be edited and were not.
/// </para>
///
/// <para>
/// Normalising here plus the unique index in <c>PromotionConfiguration</c> means
/// <c>welcome10</c> and <c>WELCOME10</c> are the same promotion, and there is exactly one row
/// holding its rate, its window and its caps together.
/// </para>
///
/// <para>
/// Whitespace is stripped rather than merely trimmed, because customers paste codes out of
/// emails and a trailing non-breaking space should not be a support ticket.
/// </para>
/// </summary>
public sealed partial class PromotionCode : ValueObject
{
    public const int MaxLength = 32;
    public const int MinLength = 3;

    public string Value { get; }

    private PromotionCode(string value) => Value = value;

    public static ErrorOr<PromotionCode> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PromotionCodeErrors.Required;
        }

        // Every kind of whitespace, not just the ends — ToUpperInvariant rather than ToUpper so
        // a Turkish-locale server cannot map "i" to a different letter than an English one and
        // turn one code into two. Onyx A44 shipped a culture-sensitive string helper for
        // exactly this class of mistake.
        var normalised = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray())
            .ToUpperInvariant();

        if (normalised.Length < MinLength)
        {
            return PromotionCodeErrors.TooShort;
        }

        if (normalised.Length > MaxLength)
        {
            return PromotionCodeErrors.TooLong;
        }

        if (!AllowedPattern().IsMatch(normalised))
        {
            return PromotionCodeErrors.InvalidFormat;
        }

        return new PromotionCode(normalised);
    }

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPattern();

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}

public static class PromotionCodeErrors
{
    public static Error Required =>
        Error.Validation("PromotionCode.Required", "A promotion code is required.");

    public static Error TooShort =>
        Error.Validation(
            "PromotionCode.TooShort",
            $"A promotion code must be at least {PromotionCode.MinLength} characters.");

    public static Error TooLong =>
        Error.Validation(
            "PromotionCode.TooLong",
            $"A promotion code may be at most {PromotionCode.MaxLength} characters.");

    public static Error InvalidFormat =>
        Error.Validation(
            "PromotionCode.InvalidFormat",
            "A promotion code may contain only letters, digits and hyphens, and must start with "
            + "a letter or digit.");
}
