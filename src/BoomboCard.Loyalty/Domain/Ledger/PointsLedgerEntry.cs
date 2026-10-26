using BoomboCard.SharedKernel;

namespace BoomboCard.Loyalty.Domain.Ledger;

public enum PointsReason
{
    /// <summary>Earned on qualifying spend. The only reason that creates points from spending.</summary>
    Earned = 0,

    /// <summary>Spent on a checkout, as a priced discount line.</summary>
    Redeemed = 1,

    /// <summary>
    /// Returned because the checkout that spent them did not complete. A positive delta, and —
    /// for the purposes of expiry — treated as an earning dated now. See
    /// <c>PointsAccount.ExpirableBefore</c>.
    /// </summary>
    Returned = 2,

    /// <summary>Lapsed past their lifetime. A negative delta written by the expiry job.</summary>
    Expired = 3,

    /// <summary>
    /// Taken back because the order that earned them was refunded. Negative, and bounded by what
    /// the earn actually gave — see <c>PointsAccount.ReverseEarning</c>.
    /// </summary>
    Reversed = 4,

    /// <summary>
    /// A manual correction or goodwill grant, by an administrator, with the actor recorded.
    /// Signed either way.
    /// </summary>
    Adjusted = 5,

    /// <summary>
    /// Forfeited because the account was closed — the customer was anonymised. Negative.
    /// </summary>
    Forfeited = 6
}

/// <summary>
/// An immutable record of one change to one points balance.
///
/// <para>
/// The counter on <see cref="PointsAccountAggregate.PointsAccount"/> is the state; these are the
/// log — the same split as <c>Product.Stock</c>/<c>StockMovement</c>,
/// <c>PasswordCredential</c>/<c>AuthAttempt</c> and <c>Promotion.RedemptionCount</c>/
/// <c>PromotionRedemption</c>. <c>SUM(Delta)</c> for an account must equal its
/// <c>Balance</c>, which is an invariant <c>PointsIntegrityCheckJob</c> asserts — the same
/// check Catalog runs over stock.
/// </para>
///
/// <para>
/// <b>This table is the whole answer to "where did my points go?"</b>, which Onyx M11 records as
/// unanswerable: the number of points redeemed was not written anywhere, on the order or
/// otherwise. Every movement here names its reason and the thing that caused it.
/// </para>
///
/// <para>
/// <b>Nothing is ever deleted or updated.</b> Expiry writes a negative row rather than removing
/// the earning it lapses; a reversal writes a negative row rather than editing the earning it
/// reverses. A balance that can be reconstructed by replaying the log is a balance a customer
/// can be shown an explanation of.
/// </para>
/// </summary>
public sealed class PointsLedgerEntry : Entity
{
    public Guid AccountId { get; private init; }

    public Guid CustomerId { get; private init; }

    /// <summary>Signed: positive for earnings and returns, negative for everything else.</summary>
    public int Delta { get; private init; }

    public PointsReason Reason { get; private init; }

    /// <summary>
    /// What caused the movement, and the idempotency key: an order id, a checkout session id, a
    /// refund id, or the administrator who made an adjustment. Same role
    /// <c>StockMovement.Reference</c> plays in Catalog, with the same unique index behind it.
    /// </summary>
    public string Reference { get; private init; } = string.Empty;

    /// <summary>The resulting balance, so the log can be audited without replaying it.</summary>
    public int BalanceAfter { get; private init; }

    public DateTimeOffset OccurredAtUtc { get; private init; }

    private PointsLedgerEntry() { }

    internal static PointsLedgerEntry Create(
        Guid accountId,
        Guid customerId,
        int delta,
        PointsReason reason,
        string reference,
        int balanceAfter,
        DateTimeOffset occurredAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            CustomerId = customerId,
            Delta = delta,
            Reason = reason,
            Reference = reference,
            BalanceAfter = balanceAfter,
            OccurredAtUtc = occurredAtUtc
        };

    public bool IsCredit => Delta > 0;
}
