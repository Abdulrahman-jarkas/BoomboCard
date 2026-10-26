using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Customers.Domain.CustomerAggregate;

/// <summary>
/// A postal address belonging to one customer.
///
/// <para>
/// An entity inside the <see cref="Customer"/> aggregate, not a value object and not its own
/// aggregate. It needs identity because a customer edits "my work address" and the edit has to
/// land on that address rather than replace the set; and it belongs inside Customer because
/// "exactly one address is the default" is an invariant across the collection, which is
/// precisely what an aggregate boundary is for.
/// </para>
///
/// <para>
/// Onyx charged $5.99 shipping and had no address of any kind, in any table — the capability
/// gap list in <c>ISSUES.md</c> §7 records "addresses · multiple addresses" under Customers and
/// "weight/dimensions (yet shipping is charged)" under Catalog. The shipping fee was a constant
/// applied to a destination nobody recorded.
/// </para>
///
/// <para>
/// <see cref="CountryCode"/> is validated against <c>Country</c>, the same smart enum Catalog
/// prices against and the quote takes its tax rate from. One definition of "countries we sell
/// into" — so an address in a country with no configured price or tax rate cannot be stored, and
/// a checkout cannot later discover it has nowhere to ship to and no rate to charge.
/// </para>
///
/// <para>
/// Which address is the default is <b>not</b> a flag here. It is
/// <c>Customer.DefaultAddressId</c>, because one column cannot hold two values and the
/// alternative — a boolean per row plus a filtered unique index — enforces "exactly one" only on
/// providers that support filtered indexes. See <c>AddressConfiguration</c>.
/// </para>
/// </summary>
public sealed class Address : Entity
{
    public const int MaxLineLength = 200;
    public const int MaxLabelLength = 50;

    public Guid CustomerId { get; private init; }

    /// <summary>What the customer calls it: "Home", "Work". Theirs, not an enum of ours.</summary>
    public string Label { get; private set; } = string.Empty;

    public string Line1 { get; private set; } = string.Empty;

    public string? Line2 { get; private set; }

    public string City { get; private set; } = string.Empty;

    public string? PostalCode { get; private set; }

    /// <summary>ISO-3166 alpha-2, validated against <c>Country</c>.</summary>
    public string CountryCode { get; private set; } = string.Empty;

    private Address() { }

    internal static ErrorOr<Address> Create(
        Guid customerId,
        string? label,
        string? line1,
        string? line2,
        string? city,
        string? postalCode,
        string? countryCode)
    {
        var country = Country.FromCode(countryCode);

        if (country.IsError)
        {
            return country.Errors;
        }

        var validated = Validate(label, line1, line2, city, postalCode);

        if (validated.IsError)
        {
            return validated.Errors;
        }

        return new Address
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Label = label!.Trim(),
            Line1 = line1!.Trim(),
            Line2 = string.IsNullOrWhiteSpace(line2) ? null : line2.Trim(),
            City = city!.Trim(),
            PostalCode = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim(),
            CountryCode = country.Value.Code
        };
    }

    internal ErrorOr<Success> Update(
        string? label,
        string? line1,
        string? line2,
        string? city,
        string? postalCode,
        string? countryCode)
    {
        var country = Country.FromCode(countryCode);

        if (country.IsError)
        {
            return country.Errors;
        }

        var validated = Validate(label, line1, line2, city, postalCode);

        if (validated.IsError)
        {
            return validated.Errors;
        }

        Label = label!.Trim();
        Line1 = line1!.Trim();
        Line2 = string.IsNullOrWhiteSpace(line2) ? null : line2.Trim();
        City = city!.Trim();
        PostalCode = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();
        CountryCode = country.Value.Code;

        return Result.Success;
    }

    /// <summary>
    /// Overwrites every line with a placeholder, in place, rather than deleting the row — an
    /// order's shipping address is part of the financial record of what was sold and where, so
    /// the row has to survive its subject's erasure.
    /// </summary>
    internal void Erase()
    {
        Label = "[erased]";
        Line1 = "[erased]";
        Line2 = null;
        City = "[erased]";
        PostalCode = null;
    }

    private static ErrorOr<Success> Validate(
        string? label,
        string? line1,
        string? line2,
        string? city,
        string? postalCode)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > MaxLabelLength)
        {
            return AddressErrors.LabelRequired;
        }

        if (string.IsNullOrWhiteSpace(line1) || line1.Trim().Length > MaxLineLength)
        {
            return AddressErrors.Line1Required;
        }

        if (string.IsNullOrWhiteSpace(city) || city.Trim().Length > MaxLineLength)
        {
            return AddressErrors.CityRequired;
        }

        if (line2?.Trim().Length > MaxLineLength || postalCode?.Trim().Length > MaxLabelLength)
        {
            return AddressErrors.FieldTooLong;
        }

        return Result.Success;
    }
}

public static class AddressErrors
{
    public static Error LabelRequired =>
        Error.Validation(
            "Address.LabelRequired",
            $"A label is required and may be at most {Address.MaxLabelLength} characters.");

    public static Error Line1Required =>
        Error.Validation(
            "Address.Line1Required",
            $"The first address line is required and may be at most {Address.MaxLineLength} characters.");

    public static Error CityRequired =>
        Error.Validation(
            "Address.CityRequired",
            $"A city is required and may be at most {Address.MaxLineLength} characters.");

    public static Error FieldTooLong =>
        Error.Validation("Address.FieldTooLong", "An address field is too long.");

    public static Error NotFound(Guid addressId) =>
        Error.NotFound("Address.NotFound", $"Address '{addressId}' was not found on this customer.");

    public static Error LimitReached =>
        Error.Validation(
            "Address.LimitReached",
            $"A customer may hold at most {Customer.MaxAddresses} addresses.");

    public static Error CannotRemoveLastDefault =>
        Error.Conflict(
            "Address.CannotRemoveLastDefault",
            "Set another address as the default before removing this one.");
}
