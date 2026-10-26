using ErrorOr;

namespace BoomboCard.SharedKernel;

/// <summary>
/// An immutable monetary amount in a stated currency, always rounded to a valid cash value.
///
/// Onyx M6/M7/A43: its pricing engine cast decimal to double, accumulated in floating point
/// and cast back, never rounded to cents (a real order total was 83.096766), and its Money
/// "value object" was a record with { get; set; } used once as Money.Zero() followed by
/// shipping.Amount = ..., discarding the currency entirely.
/// </summary>
public sealed class Money : ValueObject
{
    /// <summary>Minor units for every currency this system supports. Both are 2-decimal.</summary>
    private const int DecimalPlaces = 2;

    /// <summary>
    /// Half-up: the retail convention, and the one customers expect on a receipt.
    /// Stated explicitly so it is a decision on the record rather than a framework default.
    /// </summary>
    private const MidpointRounding Rounding = MidpointRounding.AwayFromZero;

    public decimal Amount { get; }

    public string CurrencyCode { get; }

    private Money(decimal amount, string currencyCode)
    {
        Amount = amount;
        CurrencyCode = currencyCode;
    }

    public static ErrorOr<Money> Create(decimal amount, string? currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            return MoneyErrors.CurrencyRequired;
        }

        var normalised = currencyCode.Trim().ToUpperInvariant();

        if (!Country.IsSupportedCurrency(normalised))
        {
            return MoneyErrors.UnsupportedCurrency(normalised);
        }

        if (amount < 0m)
        {
            return MoneyErrors.Negative;
        }

        return new Money(decimal.Round(amount, DecimalPlaces, Rounding), normalised);
    }

    public static Money Zero(string currencyCode) => new(0m, currencyCode.ToUpperInvariant());

    public bool IsZero => Amount == 0m;

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(decimal.Round(Amount + other.Amount, DecimalPlaces, Rounding), CurrencyCode);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(decimal.Round(Amount - other.Amount, DecimalPlaces, Rounding), CurrencyCode);
    }

    public Money MultiplyBy(int quantity) =>
        new(decimal.Round(Amount * quantity, DecimalPlaces, Rounding), CurrencyCode);

    public Money MultiplyBy(decimal rate) =>
        new(decimal.Round(Amount * rate, DecimalPlaces, Rounding), CurrencyCode);

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator *(Money money, int quantity) => money.MultiplyBy(quantity);

    public bool IsSameCurrencyAs(Money other) => CurrencyCode == other.CurrencyCode;

    private void EnsureSameCurrency(Money other)
    {
        if (!IsSameCurrencyAs(other))
        {
            throw new CurrencyMismatchException(CurrencyCode, other.CurrencyCode);
        }
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return CurrencyCode;
    }

    public override string ToString() => $"{Amount:0.00} {CurrencyCode}";
}

public static class MoneyErrors
{
    public static Error CurrencyRequired =>
        Error.Validation("Money.CurrencyRequired", "A currency code is required.");

    public static Error Negative =>
        Error.Validation("Money.Negative", "A monetary amount cannot be negative.");

    public static Error UnsupportedCurrency(string currencyCode) =>
        Error.Validation(
            "Money.UnsupportedCurrency",
            $"Currency '{currencyCode}' is not supported by any configured country.");
}
