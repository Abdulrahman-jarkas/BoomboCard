using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Loyalty.Domain.PointsAccountAggregate;

/// <summary>
/// What a point is worth, and the only place that converts between points and money.
///
/// <para>
/// <b>This value object is the fix for Onyx M11</b>, the Critical finding whose whole substance
/// is that points had no monetary value at all. <c>POST /api/orders</c> accepted
/// <c>RedeemPoints</c>, priced the order <i>without reference to it</i>, saved the total, and
/// only then subtracted the points — so the points were destroyed in exchange for nothing, and
/// the number redeemed was not recorded anywhere, so nobody could answer "where did my points
/// go?".
/// </para>
///
/// <para>
/// A loyalty scheme in which points cannot be converted to money is not a loyalty scheme. So the
/// conversion is a named, tested, single-definition value object, and a redemption produces a
/// <c>Money</c> discount line inside the quote before the total is computed.
/// </para>
/// </summary>
public static class PointsValue
{
    /// <summary>
    /// 100 points to one unit of currency, so one point is exactly one cent.
    ///
    /// <para>
    /// <b>Chosen so the conversion cannot round.</b> Both supported currencies are 2-decimal, so
    /// at this rate every whole number of points maps to an exact cash amount and back —
    /// 150 points is 1.50, never 1.4999 or 1.505. A rate like "3 points per unit" would make
    /// 100 points worth 33.333…, and that fraction would then have to be rounded somewhere, in
    /// a direction somebody has to decide, on a number the customer can check. Onyx rounded
    /// nowhere and produced order totals like <c>83.096766</c> (<b>M7</b>); the cheaper fix is
    /// to pick a rate with no fraction to round.
    /// </para>
    ///
    /// <para>
    /// If a rate has to change later it is a new constant here plus a decision about existing
    /// balances — not a new place that does arithmetic.
    /// </para>
    /// </summary>
    public const int PointsPerCurrencyUnit = 100;

    /// <summary>
    /// The least a customer may redeem at once: 100 points, one unit of currency.
    ///
    /// Not a technical limit — a redemption of 3 points is worth 0.03 and costs a quote line, a
    /// ledger row and a line on the customer's statement to say so.
    /// </summary>
    public const int MinimumRedemption = 100;

    /// <summary>
    /// The cash value of a number of points, in the stated currency.
    ///
    /// <para>
    /// Note what this does <b>not</b> do: convert between currencies. A point is worth one cent
    /// of whatever the basket is priced in, which is a deliberate simplification of the same
    /// kind <c>Country</c> makes about tax — and the alternative needs an effective-dated,
    /// auditable FX rate that this system does not have (CONCEPTS §18). It means a point is
    /// worth more in one market than another; a real scheme would set a per-currency rate here,
    /// and this is the one place that would change.
    /// </para>
    /// </summary>
    public static ErrorOr<Money> ValueOf(int points, string? currencyCode)
    {
        if (points < 0)
        {
            return PointsErrors.PointsMustBePositive;
        }

        // Exact by construction: PointsPerCurrencyUnit is 100 and the currencies are 2-decimal,
        // so this division never has a remainder to lose.
        return Money.Create((decimal)points / PointsPerCurrencyUnit, currencyCode);
    }

    /// <summary>
    /// How many points a cash amount is worth — the inverse, used when a refund has to reverse
    /// an earn computed from a now-refunded amount.
    ///
    /// Truncates rather than rounds up, so reversing can never take more points than earning
    /// gave.
    /// </summary>
    public static int PointsFor(Money amount) =>
        (int)decimal.Truncate(amount.Amount * PointsPerCurrencyUnit);

    public static bool IsRedeemable(int points) =>
        points >= MinimumRedemption;
}
