using BoomboCard.Promotions.Contracts;
using BoomboCard.Promotions.Data.Repositories;
using BoomboCard.Promotions.Domain.PromotionAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Promotions.Application.Promotions.Queries;

/// <summary>
/// Answers "is this code usable by this customer for this basket, and what is it worth?"
///
/// <para>
/// A query, so no transaction and no write. The quote calls this every time a basket changes;
/// only confirming the checkout calls <c>ReservePromotionCommand</c>. If validation reserved,
/// the last uses of a limited campaign would be burned by people browsing.
/// </para>
///
/// <para>
/// The rules themselves live in <c>Promotion.Evaluate</c>, not here, and the reservation command
/// calls the same method. That is the whole defence against Onyx <b>M18</b>: a quote and a
/// reservation with independent copies of the rules can disagree, and the customer sees a
/// discount that vanishes at checkout.
/// </para>
/// </summary>
internal sealed class ValidatePromotionQueryHandler(
    IPromotionRepository promotions,
    TimeProvider timeProvider)
    : IRequestHandler<ValidatePromotionQuery, ErrorOr<PromotionDiscountResponse>>
{
    public async Task<ErrorOr<PromotionDiscountResponse>> Handle(
        ValidatePromotionQuery query,
        CancellationToken cancellationToken)
    {
        var code = PromotionCode.Create(query.Code);

        if (code.IsError)
        {
            // A malformed code cannot match a promotion, and reporting the format rule back is
            // more useful than "not recognised" — the customer can see they pasted a space or a
            // stray character.
            return code.Errors;
        }

        var subtotal = Money.Create(query.MerchandiseSubtotal, query.CurrencyCode);

        if (subtotal.IsError)
        {
            return subtotal.Errors;
        }

        var promotion = await promotions.GetByCodeAsync(code.Value, cancellationToken);

        if (promotion is null)
        {
            return PromotionErrors.CodeNotRecognised;
        }

        // Counted in SQL, releases excluded. The aggregate does not query — it is told the count
        // and decides what the count means.
        var customerUses = await promotions.CountCustomerRedemptionsAsync(
            promotion.Id, query.CustomerId, cancellationToken);

        var scopedSubtotal = promotion.ScopedSubtotal(
            query.Lines.Select(line => (line.ProductId, line.LineTotal)),
            query.CurrencyCode);

        var discount = promotion.Evaluate(
            subtotal.Value,
            scopedSubtotal,
            customerUses,
            timeProvider.GetUtcNow());

        if (discount.IsError)
        {
            return discount.Errors;
        }

        return new PromotionDiscountResponse(
            promotion.Id,
            promotion.Code.Value,
            promotion.Description,
            discount.Value.Amount,
            scopedSubtotal.Amount,
            discount.Value.CurrencyCode,
            promotion.Stacking is StackingPolicy.Combinable);
    }
}
