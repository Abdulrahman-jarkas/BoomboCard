using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Promotions.Domain.PromotionAggregate;

public enum DiscountKind
{
    /// <summary>A fraction of the basis. Currency-agnostic.</summary>
    Percentage = 0,

    /// <summary>A stated amount in a stated currency.</summary>
    FixedAmount = 1
}

/// <summary>
/// What a promotion takes off, and the arithmetic that does it.
///
/// <para>
/// The arithmetic lives here rather than in a handler because it is the part Onyx got wrong four
/// different ways in twenty lines. Its <c>CalculateTotal</c> cast <c>decimal</c> to
/// <c>double</c>, accumulated in floating point, applied a percentage to an already-discounted
/// subtotal, and cast back without ever rounding — producing real totals like
/// <c>83.096766</c> (<b>M6</b>, <b>M7</b>). Everything here is <c>Money</c>, which is
/// <c>decimal</c>, immutable, and rounded half-up to cash on every operation.
/// </para>
/// </summary>
public sealed class Discount : ValueObject
{
    /// <summary>
    /// A percentage discount is a fraction, so 10% is <c>0.10m</c>. 100% is permitted — a free
    /// sample is a real campaign — but zero is not, because a promotion worth nothing is a
    /// configuration mistake rather than a business decision.
    /// </summary>
    public const decimal MaxRate = 1.0m;

    public DiscountKind Kind { get; private set; }

    /// <summary>Set for <see cref="DiscountKind.Percentage"/>, zero otherwise.</summary>
    public decimal Rate { get; private set; }

    /// <summary>Set for <see cref="DiscountKind.FixedAmount"/>, null otherwise.</summary>
    public Money? Amount { get; private set; }

    /// <summary>
    /// For EF, and the reason the properties above have private setters rather than being
    /// get-only like every other value object here.
    ///
    /// <para>
    /// <see cref="Amount"/> is an owned <c>Money</c>, which EF treats as a navigation — and EF
    /// cannot bind a navigation to a constructor parameter. With only the private three-argument
    /// constructor, model building failed outright with "no suitable constructor was found for
    /// the type 'Discount'". The alternative was to flatten the amount into a
    /// <c>decimal? AmountValue</c> plus a <c>string? AmountCurrency</c> and rebuild the
    /// <c>Money</c> on read, which would put currency handling back into primitives — exactly
    /// what <c>Money</c> exists to prevent (Onyx <b>A43</b>: a "value object" that was a mutable
    /// record whose currency was discarded at the one call site that used it).
    /// </para>
    ///
    /// <para>
    /// Private setters plus a private constructor keep the type immutable to every caller: the
    /// factories are the only public route in, and neither returns a mutable handle.
    /// </para>
    /// </summary>
    private Discount() { }

    private Discount(DiscountKind kind, decimal rate, Money? amount)
    {
        Kind = kind;
        Rate = rate;
        Amount = amount;
    }

    public static ErrorOr<Discount> Percentage(decimal rate)
    {
        if (rate <= 0m)
        {
            return PromotionErrors.DiscountMustBePositive;
        }

        if (rate > MaxRate)
        {
            return PromotionErrors.RateAboveOneHundredPercent;
        }

        return new Discount(DiscountKind.Percentage, rate, null);
    }

    public static ErrorOr<Discount> Fixed(Money amount)
    {
        if (amount.IsZero)
        {
            return PromotionErrors.DiscountMustBePositive;
        }

        return new Discount(DiscountKind.FixedAmount, 0m, amount);
    }

    /// <summary>
    /// Whether this discount can apply to a basket in the given currency.
    ///
    /// <para>
    /// A percentage applies anywhere: 10% off is 10% off in USD and in AED. A fixed amount is
    /// denominated, and "50 off" does not mean the same thing in two currencies — applying a
    /// USD-denominated code to an AED basket would silently hand out roughly 3.7 times the
    /// intended discount at current rates, which is the sort of arithmetic Onyx's single global
    /// <c>decimal</c> total could not even express the question about.
    /// </para>
    ///
    /// <para>
    /// The alternative — converting at some exchange rate — is deliberately not built. A rate
    /// has to come from somewhere, be effective-dated, and be auditable, and
    /// <c>Country</c> currently carries one currency per country with no FX at all (CONCEPTS
    /// §18). A fixed-amount campaign that should run in both currencies is two promotions, which
    /// is also how a merchandiser would want to price it.
    /// </para>
    /// </summary>
    public bool AppliesToCurrency(string currencyCode) =>
        Kind is DiscountKind.Percentage || Amount!.CurrencyCode == currencyCode;

    /// <summary>
    /// What this discount is worth against a basis.
    ///
    /// <para>
    /// <b>Capped at the basis.</b> A "50 off" code against a 30 basket yields 30, not 50 — the
    /// alternative is a negative subtotal, which then flows into shipping thresholds and tax and
    /// produces a total nobody can explain. Capping here rather than in the caller means every
    /// caller gets it, and there is a boundary test for the exactly-equal case.
    /// </para>
    ///
    /// <para>
    /// Rounding happens inside <c>Money</c>, once, half-up — so a 15% discount on 10.10 is 1.52
    /// and not 1.515 carried forward into the next step. Onyx rounded nowhere and its totals
    /// carried six decimal places into the payment amount.
    /// </para>
    /// </summary>
    public ErrorOr<Money> ComputeFor(Money basis)
    {
        if (!AppliesToCurrency(basis.CurrencyCode))
        {
            return PromotionErrors.CurrencyMismatch(Amount!.CurrencyCode, basis.CurrencyCode);
        }

        var raw = Kind switch
        {
            DiscountKind.Percentage => basis.MultiplyBy(Rate),
            _ => Amount!
        };

        return raw.Amount > basis.Amount ? basis : raw;
    }

    /// <summary>A merchandiser-readable rendering, used in the quote line and in reporting.</summary>
    public string Describe() =>
        Kind switch
        {
            DiscountKind.Percentage => $"{Rate * 100m:0.##}% off",
            _ => $"{Amount} off"
        };

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Kind;
        yield return Rate;
        yield return Amount;
    }

    public override string ToString() => Describe();
}
