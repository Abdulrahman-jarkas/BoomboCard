using ErrorOr;
using MediatR;

namespace BoomboCard.Promotions.Contracts;

/// <summary>
/// Claims one use of a promotion for one checkout session.
///
/// <para>
/// <b>Separate from validation, because a quote is shown many times and consumed once.</b>
/// A customer refreshing a basket page re-validates; only confirming the checkout reserves. If
/// validation reserved, the last ten promotion codes in a campaign would be burned by people
/// browsing.
/// </para>
///
/// <para>
/// <b>Idempotent on <paramref name="CheckoutSessionId"/></b>, the same reference-idempotency
/// Catalog's stock movements use, and for the same reason: it is what makes a retried checkout
/// safe and makes it the cheapest recovery path when a later step fails. A second reservation
/// for the same session returns the same success.
/// </para>
///
/// <para>
/// Issued as a command rather than driven by an event because the caller needs the answer to
/// decide what to do next — "the last one went while you were typing your card number" is a
/// normal outcome the customer is waiting on, exactly like out-of-stock.
/// </para>
/// </summary>
public sealed record ReservePromotionCommand(
    Guid PromotionId,
    Guid CustomerId,
    string CheckoutSessionId) : IRequest<ErrorOr<Success>>, IPromotionsRequest;

/// <summary>
/// Gives the use back after a checkout did not complete.
///
/// <para>
/// The counterpart to <see cref="ReservePromotionCommand"/>, and the reason the usage caps are
/// not quietly wrong over time. Without it, every abandoned checkout permanently consumes a
/// slot in a limited campaign, and a 100-use promotion runs out after 60 sales and 40 people
/// changing their minds.
/// </para>
///
/// <para>
/// Also idempotent on the session, so the Sales expiry sweeper can fire it unconditionally
/// without first asking whether anything was held — the same arrangement that makes
/// <c>RestoreStockCommand</c> safe to issue blind.
/// </para>
/// </summary>
public sealed record ReleasePromotionCommand(
    string CheckoutSessionId,
    ReleaseReason Reason) : IRequest<ErrorOr<Success>>, IPromotionsRequest;

/// <summary>
/// Why a reservation is being given back. Recorded on the ledger row so campaign reporting can
/// distinguish "people abandoned this" from "this did not convert".
/// </summary>
public enum ReleaseReason
{
    PaymentFailed = 1,
    SessionExpired = 2,
    OrderCancelled = 3,
    Refunded = 4
}
