using ErrorOr;
using MediatR;

namespace BoomboCard.Loyalty.Contracts;

/// <summary>
/// Credits points earned on an order.
///
/// <para>
/// <b>A command for now, an event subscription later.</b> This project's rule is: command when
/// the caller needs the answer to decide what to do next, event when it does not — and earning
/// points is squarely the second case. Nobody waits for it, it cannot fail for a business
/// reason, and it must be guaranteed. So the eventual shape is Loyalty subscribing to Sales'
/// <c>OrderPaidIntegrationEvent</c> through the inbox.
/// </para>
///
/// <para>
/// Sales does not exist yet, and a module cannot subscribe to a contract that has not been
/// written. The command is published now because it is needed regardless — an administrator
/// granting goodwill points uses it — and Phase 4 adds a thin inbox-guarded handler that calls
/// it. That handler is the only new code; the rule, the ledger and the idempotency are already
/// here. Recorded in <c>ROADMAP.md</c> so the gap is visible rather than implied.
/// </para>
///
/// <para>
/// <b>Idempotent on <paramref name="OrderReference"/></b>, unique at the database on
/// <c>(AccountId, Reference, Reason)</c> — the same mechanism that makes a retried stock
/// deduction safe. A redelivered "order paid" message credits once.
/// </para>
///
/// <para>
/// <c>QualifyingSpend</c> is <b>merchandise after discounts, excluding tax and shipping</b>. The
/// caller computes it; the definition and the reasons for each exclusion are in
/// <c>EarnRule.PointsFor</c>. Passing the gross total would pay loyalty on tax, which is money
/// collected on behalf of a government.
/// </para>
/// </summary>
public sealed record GrantPointsCommand(
    Guid CustomerId,
    decimal QualifyingSpend,
    string CurrencyCode,
    string OrderReference) : IRequest<ErrorOr<int>>, ILoyaltyRequest;

/// <summary>
/// Debits points a checkout is spending.
///
/// <para>
/// Separate from validation, because a quote is shown many times and consumed once — the same
/// split Promotions makes between <c>ValidatePromotionQuery</c> and
/// <c>ReservePromotionCommand</c>, and for the same reason. If validation debited, a customer
/// refreshing a basket page would drain their own balance.
/// </para>
///
/// <para>
/// <b>Idempotent on <paramref name="CheckoutSessionId"/></b>, so a retried checkout debits
/// once. This command is what commits in the same transaction as the order — the half of
/// <b>D4</b> that the quote-time validation does not cover.
/// </para>
/// </summary>
public sealed record RedeemPointsCommand(
    Guid CustomerId,
    int Points,
    string CheckoutSessionId) : IRequest<ErrorOr<Success>>, ILoyaltyRequest;

/// <summary>
/// Gives back points a checkout spent but did not complete.
///
/// <para>
/// Safe to call unconditionally, like <c>ReleasePromotionCommand</c> and
/// <c>RestoreStockCommand</c>: Sales' expiry sweeper is told a session id and fires blind
/// without knowing whether any points were spent. A session that redeemed nothing is a success
/// returning nothing.
/// </para>
/// </summary>
public sealed record ReleasePointsCommand(
    string CheckoutSessionId,
    PointsReleaseReason Reason) : IRequest<ErrorOr<Success>>, ILoyaltyRequest;

/// <summary>
/// Why a redemption is being given back. Recorded on the ledger entry's reference so a
/// statement can explain it.
/// </summary>
public enum PointsReleaseReason
{
    PaymentFailed = 1,
    SessionExpired = 2,
    OrderCancelled = 3
}

/// <summary>
/// Takes back points an order earned, because that order has been refunded.
///
/// <para>
/// The reversal is bounded by what the earning actually gave <b>and</b> clamped to the current
/// balance — a customer who earned 500 and spent 400 has 100 left, and a refund cannot claw back
/// what is already gone without a negative balance, which this scheme does not represent. See
/// <c>PointsAccount.ReverseEarning</c>.
/// </para>
///
/// <para>
/// <c>ISSUES.md</c> §7 lists "reversal on refund" as a missing capability, and <b>M12</b>'s
/// obligation line names it explicitly: "an explicit earn rule … with reversal on refund".
/// </para>
/// </summary>
public sealed record ReverseEarnedPointsCommand(
    Guid CustomerId,
    string OrderReference,
    string RefundReference) : IRequest<ErrorOr<Success>>, ILoyaltyRequest;

/// <summary>
/// A manual correction or goodwill grant. Administrator only, and the actor is recorded on the
/// ledger entry rather than taken from the request — an actor a caller supplies is one a caller
/// can forge, and the only reason to record one is to be able to trust it afterwards.
/// </summary>
public sealed record AdjustPointsCommand(Guid CustomerId, int Points, string Note)
    : IRequest<ErrorOr<Success>>, ILoyaltyRequest;
