using BoomboCard.SharedKernel;

namespace BoomboCard.Loyalty.Domain.PointsAccountAggregate;

/// <summary>
/// How spending turns into points.
///
/// <para>
/// <b>This is the fix for Onyx M12</b>: "no code path anywhere increases
/// <c>Customer.LoyaltyPoints</c>". The only balances that existed were typed into
/// <c>DbSeeder</c> — 500, 120 and 40 — so a new customer started at zero and stayed there
/// permanently. The scheme had a spend side and no earn side at all.
/// </para>
///
/// <para>
/// One definition, stated once, in the one place that does the arithmetic.
/// </para>
/// </summary>
public static class EarnRule
{
    /// <summary>
    /// One point per whole unit of qualifying spend — so a 49.99 order earns 49 points, worth
    /// 0.49 back. A 0.5% effective return.
    ///
    /// <para>
    /// Truncated rather than rounded, deliberately and in the house's favour by at most one
    /// point. The alternative is a scheme where 49.50 earns 50 points, which is fine until
    /// somebody notices that splitting an order into halves earns more than placing it once —
    /// and gaming a rounding rule is a real thing customers do.
    /// </para>
    /// </summary>
    public const int PointsPerCurrencyUnit = 1;

    /// <summary>
    /// How long earned points live. Twelve months from the moment they were earned.
    ///
    /// <para>
    /// Expiry exists because unexpired points are an unbounded liability on the balance sheet
    /// with no end date — and because <c>ISSUES.md</c> §7 lists it as a missing capability.
    /// </para>
    /// </summary>
    public static readonly TimeSpan PointsLifetime = TimeSpan.FromDays(365);

    /// <summary>
    /// Points earned on an amount of qualifying spend.
    ///
    /// <para>
    /// <b>What counts as qualifying spend is the interesting half</b>, and it is the caller's
    /// job to pass the right number — so the definition is stated here and asserted by the
    /// Sales quote that calls it: <b>merchandise after discounts, excluding tax and shipping</b>.
    /// </para>
    ///
    /// <para>
    /// Each exclusion is a decision. Tax is not revenue — it is money collected on behalf of a
    /// government, and paying loyalty on it means paying a reward out of somebody else's money.
    /// Shipping is a cost passed through, so earning points on it rewards buying from further
    /// away. And discounts are subtracted first, because earning on the pre-discount figure
    /// means a promotion silently costs margin twice — once as the discount and again as the
    /// points earned on money nobody paid. Onyx handed out a 5% volume discount and promo codes
    /// with no cost price recorded anywhere, so it could not have noticed either (its own note
    /// on <c>Product.CostPrice</c>).
    /// </para>
    /// </summary>
    public static int PointsFor(Money qualifyingSpend) =>
        (int)decimal.Truncate(qualifyingSpend.Amount) * PointsPerCurrencyUnit;
}
