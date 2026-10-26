using BoomboCard.Promotions.Contracts;
using BoomboCard.SharedKernel;

namespace BoomboCard.Promotions.Domain.Redemptions;

/// <summary>
/// One claimed use of one promotion, by one customer, for one checkout session.
///
/// <para>
/// The counter on <c>Promotion</c> is the state; these are the log — the same split as
/// <c>Product.Stock</c> / <c>StockMovement</c> and <c>PasswordCredential</c> /
/// <c>AuthAttempt</c>, for the same two reasons. The rows grow without bound, so they cannot be
/// a collection on the aggregate; and the total cap is an invariant that has to be enforced
/// under the concurrency token, so the count has to be state rather than a <c>COUNT</c>
/// recomputed per checkout.
/// </para>
///
/// <para>
/// <b>Two unique indexes do the work that no counter can.</b> See
/// <c>PromotionRedemptionConfiguration</c> — one makes a retried checkout a no-op, the other
/// enforces the per-customer cap at the database rather than in application code with a race in
/// the middle.
/// </para>
///
/// <para>
/// A release does not delete the row. It marks it, so campaign reporting can tell "100 people
/// redeemed this" from "140 tried and 40 abandoned" — a distinction Onyx could not make, because
/// it recorded a promo code as a nullable string on the order and nothing else (<b>M16</b>, and
/// the campaign-performance gap in <c>ISSUES.md</c> §7).
/// </para>
/// </summary>
public sealed class PromotionRedemption : Entity
{
    public Guid PromotionId { get; private init; }

    public Guid CustomerId { get; private init; }

    /// <summary>
    /// The checkout session id. Both the correlation handle back to the order and the
    /// idempotency key — the same role <c>StockMovement.Reference</c> plays in Catalog.
    /// </summary>
    public string CheckoutSessionId { get; private init; } = string.Empty;

    /// <summary>
    /// This customer's n-th use of this promotion, counting from 1.
    ///
    /// <para>
    /// <b>This integer is how the per-customer cap is enforced by the database.</b> A cap is a
    /// count, and a count cannot be a unique index — so the ordinal turns it into one:
    /// <c>(PromotionId, CustomerId, CustomerOrdinal)</c> is unique, the handler computes the
    /// ordinal as "uses so far plus one", and two concurrent reservations that both read the same
    /// number compute the same ordinal and collide. One wins; the loser is retried and reads the
    /// new count.
    /// </para>
    ///
    /// <para>
    /// The alternative, a filtered unique index on <c>(PromotionId, CustomerId)</c> when the cap
    /// happens to be 1, needs provider-specific SQL in a provider-agnostic module and only works
    /// for a cap of one. That mistake was made and reverted once already, in Phase 1's address
    /// defaulting.
    /// </para>
    /// </summary>
    public int CustomerOrdinal { get; private init; }

    public DateTimeOffset RedeemedAtUtc { get; private init; }

    public DateTimeOffset? ReleasedAtUtc { get; private set; }

    public ReleaseReason? ReleaseReason { get; private set; }

    public bool IsReleased => ReleasedAtUtc is not null;

    private PromotionRedemption() { }

    internal static PromotionRedemption Claim(
        Guid promotionId,
        Guid customerId,
        string checkoutSessionId,
        int customerOrdinal,
        DateTimeOffset nowUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            PromotionId = promotionId,
            CustomerId = customerId,
            CheckoutSessionId = checkoutSessionId,
            CustomerOrdinal = customerOrdinal,
            RedeemedAtUtc = nowUtc
        };

    internal void Release(ReleaseReason reason, DateTimeOffset nowUtc)
    {
        // Idempotent: releasing twice keeps the first reason and the first instant. The sweeper
        // fires unconditionally, so this is the normal path rather than an edge case.
        if (IsReleased)
        {
            return;
        }

        ReleasedAtUtc = nowUtc;
        ReleaseReason = reason;
    }
}
