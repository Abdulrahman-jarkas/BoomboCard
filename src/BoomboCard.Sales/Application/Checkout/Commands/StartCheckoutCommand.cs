using BoomboCard.Catalog.Contracts;
using BoomboCard.Customers.Contracts;
using BoomboCard.Loyalty.Contracts;
using BoomboCard.Promotions.Contracts;
using BoomboCard.Sales.Contracts;
using BoomboCard.Sales.Data;
using BoomboCard.Sales.Data.Repositories;
using BoomboCard.Sales.Domain.CheckoutSessionAggregate;
using BoomboCard.Sales.Domain.Pricing;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Sales.Application.Checkout.Commands;

/// <summary>One basket line, as the client sends it.</summary>
public sealed record CheckoutLineRequest(Guid ProductId, int Quantity);

/// <summary>
/// Starts a checkout: prices the basket and creates the session. <b>TX 0 of the flow — the
/// anchor.</b>
///
/// <para>
/// <b>No money value crosses the wire inbound.</b> The request carries product ids, quantities,
/// a country, an optional promotion code and an optional number of points. Every price comes from
/// Catalog's <c>GetProductPricingQuery</c> inside this handler, every discount from the module
/// that owns it, and the total from <c>QuoteCalculator</c>. That is <b>M2</b> — "the client never
/// sends a money value" — and it is the reason the amount charged in Phase 5 can be derived from
/// the server-held session rather than trusted from a request.
/// </para>
///
/// <para>
/// Onyx's equivalent was <c>POST /api/orders</c> with a <c>CreateOrderRequest</c> that carried
/// <c>RedeemPoints</c> and a <c>PromoCode</c>, and its handler created the order row before
/// pricing anything.
/// </para>
/// </summary>
public sealed record StartCheckoutCommand(
    Guid CustomerId,
    string CountryCode,
    IReadOnlyList<CheckoutLineRequest> Lines,
    string? PromotionCode = null,
    int PointsToRedeem = 0) : IRequest<ErrorOr<Guid>>, ISalesRequest;

internal sealed class StartCheckoutCommandHandler(
    ISalesRepository sales,
    IUnitOfWork<SalesDbContext> unitOfWork,
    ISender sender,
    TimeProvider timeProvider)
    : IRequestHandler<StartCheckoutCommand, ErrorOr<Guid>>
{
    public async Task<ErrorOr<Guid>> Handle(
        StartCheckoutCommand command,
        CancellationToken cancellationToken)
    {
        var country = Country.FromCode(command.CountryCode);

        if (country.IsError)
        {
            return country.Errors;
        }

        var shipping = ShippingPolicy.ForCountry(country.Value);

        if (shipping.IsError)
        {
            return shipping.Errors;
        }

        if (command.Lines.Count == 0)
        {
            return CheckoutErrors.NoLines;
        }

        // ---- the customer must be able to transact.
        //
        // One question, answered by the module that owns it, using the single definition of
        // "may this customer buy something" that Customers publishes — rather than Sales
        // re-deriving "active and verified" and getting it subtly different. Onyx M18 is the
        // warning about one rule written twice.
        var customer = await sender.Send(
            new GetCustomerSummaryQuery(command.CustomerId), cancellationToken);

        if (customer.IsError)
        {
            return customer.Errors;
        }

        if (!customer.Value.CanTransact)
        {
            return CheckoutErrors.CustomerCannotTransact;
        }

        // ---- prices and availability, from Catalog, in ONE round trip.
        //
        // Not a lookup per line. Onyx A26's shape was the opposite — a query per item inside a
        // loop — and its order creation did exactly that, with a SaveChanges inside the loop too.
        var productIds = command.Lines.Select(line => line.ProductId).Distinct().ToList();

        var pricing = await sender.Send(
            new GetProductPricingQuery(productIds, country.Value.Code), cancellationToken);

        if (pricing.IsError)
        {
            return pricing.Errors;
        }

        var priced = pricing.Value.ToDictionary(product => product.ProductId);

        var quotedLines = new List<QuotedLine>(command.Lines.Count);

        foreach (var requested in command.Lines)
        {
            if (!priced.TryGetValue(requested.ProductId, out var product))
            {
                return CheckoutErrors.ProductNotFound(requested.ProductId);
            }

            if (!product.IsActive)
            {
                return CheckoutErrors.ProductUnavailable(product.Sku);
            }

            // The PriceId is captured here and carried onto the line. It is what turns "the
            // price the customer was shown" from an assertion into something provable — Onyx
            // A6 served browsing from a stale cache and ordering from the live row.
            quotedLines.Add(new QuotedLine(
                product.ProductId,
                product.Sku,
                product.Name,
                requested.Quantity,
                product.Amount,
                product.PriceId));
        }

        // ---- the promotion, priced by Promotions.
        //
        // Validated, not reserved. A quote is shown many times and consumed once; reserving here
        // would burn the last uses of a limited campaign on people browsing.
        PromotionInput? promotion = null;

        if (!string.IsNullOrWhiteSpace(command.PromotionCode))
        {
            var merchandiseSubtotal = quotedLines.Sum(line => line.UnitPrice * line.Quantity);

            var basketLines = quotedLines
                .Select(line => new BasketLine(
                    line.ProductId, line.Quantity, line.UnitPrice * line.Quantity))
                .ToList();

            var discount = await sender.Send(
                new ValidatePromotionQuery(
                    command.PromotionCode,
                    command.CustomerId,
                    country.Value.CurrencyCode,
                    merchandiseSubtotal,
                    basketLines),
                cancellationToken);

            // The reason is returned verbatim: "your basket is below the $50 minimum" is the
            // whole value of the response, and the deliberate contrast with the sign-in
            // endpoint's uniform error. Onyx M15 returned a 0% rate and let the order through.
            if (discount.IsError)
            {
                return discount.Errors;
            }

            promotion = new PromotionInput(
                discount.Value.PromotionId,
                $"{discount.Value.Code} — {discount.Value.Description}",
                discount.Value.DiscountAmount);
        }

        // ---- the loyalty redemption, priced by Loyalty.
        //
        // Validated here and debited later, which is the D4 fix: Onyx checked sufficiency inside
        // the debit, after the order was committed, so an insufficient balance was a 500 with
        // the order standing.
        LoyaltyInput? loyalty = null;

        if (command.PointsToRedeem > 0)
        {
            var points = await sender.Send(
                new ValidatePointsRedemptionQuery(
                    command.CustomerId, command.PointsToRedeem, country.Value.CurrencyCode),
                cancellationToken);

            if (points.IsError)
            {
                return points.Errors;
            }

            loyalty = new LoyaltyInput(points.Value.Points, points.Value.DiscountAmount);
        }

        // ---- the session, priced at creation.
        var session = CheckoutSession.Start(
            command.CustomerId,
            country.Value,
            shipping.Value,
            quotedLines,
            promotion,
            loyalty,
            timeProvider.GetUtcNow());

        if (session.IsError)
        {
            return session.Errors;
        }

        sales.Add(session.Value);

        // One SaveChanges. The session, its lines and its adjustments commit together or not at
        // all — and this commit is TX 0, which must land BEFORE anything is held, so every hold
        // placed in another module names a session that exists.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return session.Value.Id;
    }
}
