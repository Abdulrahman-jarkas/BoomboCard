using ErrorOr;

namespace BoomboCard.SharedKernel;

/// <summary>
/// The countries this system sells into, with their currency and sales-tax rate.
///
/// Hardcoded on purpose, and in exactly one place. Onyx M14 held WELCOME10's expiry as
/// 2026-12-31 in OrderPricingService and 2026-11-30 in NightlyReportJob, so from December
/// the report would have shown zero uptake on a code still costing margin. A single
/// definition makes that class of divergence impossible.
///
/// Tax is applied at checkout, never to a displayed catalogue price: a ProductPrice is the
/// tax-exclusive figure shown to the customer, and the quote adds tax on top.
///
/// When a rate must vary by category, or change over a date range, this graduates to a table
/// with effective dating. It is not that yet, and pretending otherwise would be the
/// speculative generality Onyx was full of (A45: four unused "seams" left for a next PR).
/// </summary>
public sealed class Country : ValueObject
{
    public static readonly Country UnitedStates = new("US", "United States", "USD", 0.08m);

    public static readonly Country UnitedArabEmirates = new("AE", "United Arab Emirates", "AED", 0.05m);

    public static readonly IReadOnlyList<Country> All = [UnitedStates, UnitedArabEmirates];

    public string Code { get; }

    public string Name { get; }

    public string CurrencyCode { get; }

    /// <summary>Sales tax / VAT as a fraction, e.g. 0.05m for 5%.</summary>
    public decimal TaxRate { get; }

    private Country(string code, string name, string currencyCode, decimal taxRate)
    {
        Code = code;
        Name = name;
        CurrencyCode = currencyCode;
        TaxRate = taxRate;
    }

    public static ErrorOr<Country> FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return CountryErrors.CodeRequired;
        }

        var normalised = code.Trim().ToUpperInvariant();
        var match = All.FirstOrDefault(country => country.Code == normalised);

        return match is null ? CountryErrors.NotSupported(normalised) : match;
    }

    public static bool IsSupported(string? code) =>
        code is not null && All.Any(country => country.Code == code.Trim().ToUpperInvariant());

    public static bool IsSupportedCurrency(string? currencyCode) =>
        currencyCode is not null
        && All.Any(country => country.CurrencyCode == currencyCode.Trim().ToUpperInvariant());

    /// <summary>Tax due on a tax-exclusive amount, in the same currency.</summary>
    public Money TaxOn(Money taxExclusiveAmount) => taxExclusiveAmount.MultiplyBy(TaxRate);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Code;
    }

    public override string ToString() => Code;
}

public static class CountryErrors
{
    public static Error CodeRequired =>
        Error.Validation("Country.CodeRequired", "A country code is required.");

    public static Error NotSupported(string code) =>
        Error.Validation("Country.NotSupported", $"Country '{code}' is not supported.");
}
