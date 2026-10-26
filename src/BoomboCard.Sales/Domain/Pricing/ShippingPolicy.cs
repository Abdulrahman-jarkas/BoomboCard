using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Sales.Domain.Pricing;

/// <summary>
/// What shipping costs, when it is free, and whether it is taxed.
///
/// <para>
/// <b>Three Onyx findings live in this one class</b>, and each needed a different kind of fix.
/// </para>
///
/// <para>
/// <b>M9 — one constant, one comparison.</b> Onyx used the same $75 figure twice with two
/// different operators: <c>subtotal &gt; 75</c> for its volume discount and
/// <c>subtotal &gt;= 75</c> for free shipping. So exactly $75.00 got free shipping and no
/// discount, and the two rules disagreed about the number they shared. Here there is one named
/// threshold and one comparison, used by one method, with boundary tests either side of it.
/// </para>
///
/// <para>
/// <b>M8 — the basis is named.</b> See <see cref="QualifiesForFreeShipping"/>.
/// </para>
///
/// <para>
/// <b>M10 — taxability is an explicit, recorded policy.</b> The register's complaint is precise:
/// Onyx applied tax to goods and added shipping afterwards, and "this may be intentional for the
/// jurisdiction, but it is undocumented and untested, so it cannot be distinguished from a bug."
/// So <see cref="ShippingIsTaxable"/> is a stated per-country decision with the jurisdiction
/// rule written down next to it, and the tax step reads it rather than assuming either way.
/// </para>
///
/// <para>
/// Hardcoded per country and in exactly one place, like <c>Country</c>'s tax rate, and for the
/// same reason: two countries with genuinely different rules is enough to exercise both paths,
/// and inventing a rate table with effective dating that nothing has asked for is the
/// speculative generality <b>A45</b> is about. When a rule has to vary by weight, destination
/// zone or carrier, this graduates to a table.
/// </para>
/// </summary>
public sealed class ShippingPolicy : ValueObject
{
    /// <summary>
    /// US: $5.99 flat, free over $75, and <b>shipping is taxable</b>.
    ///
    /// The jurisdiction rule, recorded as M10 requires: most US states that tax tangible goods
    /// also tax the delivery charge when the goods themselves are taxable, on the basis that
    /// delivery is part of the sale price. This system charges one blended 8% rate rather than
    /// modelling states, so it follows the majority rule.
    /// </summary>
    public static readonly ShippingPolicy UnitedStates = new(
        countryCode: "US",
        flatRate: 5.99m,
        freeAbove: 75.00m,
        shippingIsTaxable: true);

    /// <summary>
    /// AE: AED 25 flat, free over AED 200, and <b>shipping is taxable</b>.
    ///
    /// UAE VAT treats a delivery charge as part of the consideration for a taxable supply, so it
    /// carries the same 5% rate as the goods.
    /// </summary>
    public static readonly ShippingPolicy UnitedArabEmirates = new(
        countryCode: "AE",
        flatRate: 25.00m,
        freeAbove: 200.00m,
        shippingIsTaxable: true);

    private static readonly IReadOnlyList<ShippingPolicy> All = [UnitedStates, UnitedArabEmirates];

    public string CountryCode { get; }

    /// <summary>The charge when the basket does not qualify for free shipping.</summary>
    public decimal FlatRate { get; }

    /// <summary>
    /// The threshold. <b>One constant</b>, compared in <b>one place</b> — see
    /// <see cref="QualifiesForFreeShipping"/>.
    /// </summary>
    public decimal FreeAbove { get; }

    /// <summary>
    /// Whether the shipping charge is part of the tax base. See the class remarks for M10.
    /// </summary>
    public bool ShippingIsTaxable { get; }

    private ShippingPolicy(
        string countryCode,
        decimal flatRate,
        decimal freeAbove,
        bool shippingIsTaxable)
    {
        CountryCode = countryCode;
        FlatRate = flatRate;
        FreeAbove = freeAbove;
        ShippingIsTaxable = shippingIsTaxable;
    }

    public static ErrorOr<ShippingPolicy> ForCountry(Country country)
    {
        var match = All.FirstOrDefault(policy => policy.CountryCode == country.Code);

        return match is null
            ? Error.Validation(
                "Shipping.NoPolicy",
                $"No shipping policy is configured for '{country.Code}'.")
            : match;
    }

    /// <summary>
    /// Whether a basket ships free — <b>decided on the merchandise subtotal, before any
    /// discount.</b>
    ///
    /// <para>
    /// <b>This single choice is the whole of M8.</b> Onyx decided shipping on the
    /// <i>post-discount</i> subtotal, which made its totals non-monotonic: spending more could
    /// cost less, and spending a little more could cost more. Its own register tabulates the
    /// hole:
    /// </para>
    ///
    /// <code>
    /// $75.00 -> no discount (needs &gt; 75) -> free shipping -> $81.00
    /// $76.00 -> $72.20 after 5%            -> +$5.99        -> $83.97
    /// $80.00 -> $76.00 after 5%            -> free shipping -> $82.08
    /// </code>
    ///
    /// <para>
    /// Every subtotal in <c>(75.00, 78.95)</c> fell into it, so the free-shipping incentive
    /// actively worked against the business there — a customer adding an item to reach free
    /// delivery could end up paying more.
    /// </para>
    ///
    /// <para>
    /// Evaluating against the pre-discount merchandise subtotal fixes it because that figure
    /// rises monotonically with what the customer puts in the basket, and no later step can pull
    /// it back below the threshold. Choosing post-discount would need the threshold applied to a
    /// number that discounts can lower, which is the bug. The decision is named here, asserted
    /// by <c>Quote</c>'s monotonicity property test over 0–500 in 1-cent steps, and recorded in
    /// the API documentation so a consumer can see which basis is used.
    /// </para>
    /// </summary>
    public bool QualifiesForFreeShipping(Money merchandiseSubtotal) =>
        merchandiseSubtotal.Amount >= FreeAbove;

    /// <summary>The charge for a basket, in the basket's currency.</summary>
    public Money ChargeFor(Money merchandiseSubtotal)
    {
        var currency = merchandiseSubtotal.CurrencyCode;

        return QualifiesForFreeShipping(merchandiseSubtotal)
            ? Money.Zero(currency)
            : Money.Create(FlatRate, currency).Value;
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return CountryCode;
    }

    public override string ToString() =>
        $"{CountryCode}: {FlatRate:0.00} flat, free above {FreeAbove:0.00}, "
        + $"{(ShippingIsTaxable ? "taxable" : "not taxable")}";
}
