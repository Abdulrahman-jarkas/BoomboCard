using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Loyalty.Contracts;

/// <summary>
/// Marks a request as a Loyalty command: it mutates Loyalty state, and so must run inside a
/// transaction on Loyalty's context.
///
/// See <see cref="IUnitOfWork{TScope}"/> for why the module has to be carried by the request
/// type, and <c>ModuleWiringTests</c> for what asserts it.
/// </summary>
public interface ILoyaltyRequest : ITransactionalRequest;

/// <summary>
/// A customer's balance and what it is worth.
///
/// <para>
/// <c>Value</c> is the whole point, and the direct answer to Onyx <b>M11</b>: its points had no
/// monetary value at all, so <c>LoyaltyPoints = 500</c> was a number with no meaning. Here the
/// conversion is a single definition (<c>PointsValue</c>) and the balance is reported alongside
/// what it would be worth on a basket in that currency.
/// </para>
/// </summary>
public sealed record PointsBalanceResponse(
    Guid CustomerId,
    int Balance,
    decimal Value,
    string CurrencyCode,
    int MinimumRedemption,
    bool CanRedeem);

/// <summary>
/// A customer's balance, valued in the given currency.
///
/// A point is worth one cent of whatever the basket is priced in — see <c>PointsValue</c> for
/// why that is a deliberate simplification rather than an oversight.
/// </summary>
public sealed record GetPointsBalanceQuery(Guid CustomerId, string CurrencyCode)
    : IRequest<ErrorOr<PointsBalanceResponse>>;

/// <summary>
/// What redeeming a number of points would be worth as a discount.
///
/// <para>
/// <b>This record is the shape of the M11 fix.</b> Onyx accepted <c>RedeemPoints</c> on the
/// order request, priced the order <i>without reference to it</i>, saved the total, and only
/// then subtracted the points — so they bought nothing. A redemption here produces a priced
/// discount the quote adds as a line <i>before</i> computing the total, and
/// <c>DiscountAmount</c> is already rounded to cash so the caller recomputes nothing.
/// </para>
/// </summary>
public sealed record PointsDiscountResponse(
    int Points,
    decimal DiscountAmount,
    string CurrencyCode,
    int BalanceAfter);

/// <summary>
/// "Could this customer redeem these points, and what would it be worth?"
///
/// <para>
/// A query: no transaction, no write. <b>This is the D4 fix.</b> Onyx checked balance
/// sufficiency inside the debit, which ran after the order was committed — so an insufficient
/// balance threw an <c>InvalidOperationException</c> that surfaced as a 500 while the order and
/// every stock decrement stood. Validation happens here, during the quote, before anything is
/// written; the debit is <see cref="RedeemPointsCommand"/> and commits with the order.
/// </para>
///
/// <para>
/// Both use the same rule — <c>PointsAccount.CanRedeem</c> — so a quote cannot promise a
/// discount the debit then refuses. Onyx <b>M18</b> is the warning about writing one rule twice.
/// </para>
/// </summary>
public sealed record ValidatePointsRedemptionQuery(
    Guid CustomerId,
    int Points,
    string CurrencyCode) : IRequest<ErrorOr<PointsDiscountResponse>>;

/// <summary>
/// One line of a customer's points statement.
///
/// <para>
/// <c>ISSUES.md</c> §7 lists "a points statement" as a capability that does not exist, and Onyx
/// could not have built one: the balance was a single mutable <c>int</c> with no history, so
/// there was nothing to list. Every row here names its reason and the thing that caused it —
/// which is the answer to M11's "there is no way to answer 'where did my points go?'".
/// </para>
/// </summary>
public sealed record PointsStatementLine(
    int Delta,
    string Reason,
    string Reference,
    int BalanceAfter,
    DateTimeOffset OccurredAtUtc);

public sealed record PointsStatementResponse(
    Guid CustomerId,
    int Balance,
    IReadOnlyList<PointsStatementLine> Lines,
    string? NextCursor);

/// <summary>
/// A page of the statement. <c>Cursor</c> is the previous page's last <c>occurredAtUtc</c> in
/// ISO-8601, or null for the first page; <c>Limit</c> is capped by the handler.
/// </summary>
public sealed record GetPointsStatementQuery(
    Guid CustomerId,
    string? Cursor = null,
    int Limit = 50) : IRequest<ErrorOr<PointsStatementResponse>>;
