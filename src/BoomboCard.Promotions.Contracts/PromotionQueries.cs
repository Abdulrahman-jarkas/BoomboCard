using ErrorOr;
using MediatR;

namespace BoomboCard.Promotions.Contracts;

/// <summary>
/// One basket line, as Promotions needs to see it: which product, how many, and what the line
/// comes to.
///
/// <para>
/// The caller supplies the amounts because the caller is Sales, which has already captured them
/// from Catalog's <c>GetProductPricingQuery</c> — including the <c>PriceId</c> that proves what
/// the customer was shown. Promotions re-deriving them would mean two modules holding an opinion
/// about what a line costs, which is Onyx <b>A6</b> ("browsing and buying read prices from
/// different sources") rebuilt one boundary further out.
/// </para>
///
/// <para>
/// Note that this is a <i>server-held</i> amount being passed between two server-side modules,
/// never a client-supplied one. <b>M2</b> — "the client never sends a money value" — is about the
/// wire, and it holds: the checkout request carries product ids and quantities only.
/// </para>
/// </summary>
public sealed record BasketLine(Guid ProductId, int Quantity, decimal LineTotal);

/// <summary>
/// What applying a code would be worth.
///
/// <para>
/// <c>DiscountAmount</c> is already rounded to cash and already capped at the basis, so the
/// caller adds it as a line and does not recompute anything. <c>PromotionId</c> is what the
/// subsequent <see cref="ReservePromotionCommand"/> names.
/// </para>
///
/// <para>
/// <c>AppliedToAmount</c> is the basis the discount was calculated against, echoed back so the
/// quote can record what it was. A discount with no stated basis is unauditable — Onyx computed
/// its total in one expression with four interleaved rules and stored a single
/// <c>decimal Total</c>, so nothing could say afterwards which rule had produced which part of
/// it (<b>M8</b>, <b>M19</b>).
/// </para>
/// </summary>
public sealed record PromotionDiscountResponse(
    Guid PromotionId,
    string Code,
    string Description,
    decimal DiscountAmount,
    decimal AppliedToAmount,
    string CurrencyCode,
    bool CombinesWithOtherPromotions);

/// <summary>
/// "Is this code usable by this customer for this basket, and what is it worth?"
///
/// <para>
/// A query: it takes no transaction and changes nothing. Reserving the use is a separate
/// command, because a quote is shown many times and consumed once.
/// </para>
///
/// <para>
/// <b>Failures name their reason</b>, which is the opposite of the sign-in endpoint's uniform
/// error and deliberately so. A promotion code is not a credential: the customer typed it, they
/// know they typed it, and "your basket is below the £50 minimum" is the entire value of the
/// response. Onyx <b>M15</b> returned a 0% discount rate for an unrecognised code and let the
/// order through as though the customer had used a valid one — so a mistyped code silently
/// became no discount and no explanation.
/// </para>
///
/// <para>
/// <c>CustomerId</c> is needed because per-customer usage caps exist: a promotion can be valid
/// in general and exhausted for one person. <c>CurrencyCode</c> is the basket's — a fixed-amount
/// promotion is denominated in one currency and does not apply to a basket in another, while a
/// percentage promotion applies to any.
/// </para>
/// </summary>
public sealed record ValidatePromotionQuery(
    string Code,
    Guid CustomerId,
    string CurrencyCode,
    decimal MerchandiseSubtotal,
    IReadOnlyList<BasketLine> Lines) : IRequest<ErrorOr<PromotionDiscountResponse>>;
