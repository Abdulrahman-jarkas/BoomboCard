using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Customers.Domain.CustomerAggregate;

/// <summary>
/// A person's name, as they gave it.
///
/// <para>
/// One field, not a first/last pair. Splitting a name into two required columns is a
/// correctness bug for a large fraction of the world's names, and the only thing this system
/// does with a name is address a message. Whitespace is collapsed so that
/// <c>"Ada   Lovelace"</c> and <c>"Ada Lovelace"</c> are the same value, but nothing else is
/// "corrected" — Onyx A44 shipped a <c>ToTitleCase</c> helper over <c>CurrentCulture</c>, which
/// would have rendered <c>McDonald</c> as <c>Mcdonald</c> and <c>o'brien</c> as <c>O'Brien</c>
/// or not depending on the server's locale. It was never called, which is the only reason it
/// never corrupted anyone's name.
/// </para>
/// </summary>
public sealed class PersonName : ValueObject
{
    public const int MaxLength = 200;

    public string Value { get; }

    private PersonName(string value) => Value = value;

    public static ErrorOr<PersonName> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PersonNameErrors.Required;
        }

        var collapsed = string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return collapsed.Length > MaxLength
            ? PersonNameErrors.TooLong
            : new PersonName(collapsed);
    }

    /// <summary>
    /// The placeholder an anonymised customer's name becomes. A constant rather than an empty
    /// string, so a report rendering it shows that erasure happened instead of a blank cell that
    /// reads as missing data.
    /// </summary>
    public static PersonName Anonymised => new("[erased]");

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}

public static class PersonNameErrors
{
    public static Error Required =>
        Error.Validation("Name.Required", "A name is required.");

    public static Error TooLong =>
        Error.Validation(
            "Name.TooLong",
            $"A name may be at most {PersonName.MaxLength} characters.");
}
