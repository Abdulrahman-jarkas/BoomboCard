using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Contracts.Authorization;
using BoomboCard.Sales.Application.Checkout.Commands;
using BoomboCard.Sales.Application.Orders.Commands;
using BoomboCard.Sales.Application.Orders.Queries;
using BoomboCard.Sales.Contracts;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BoomboCard.Sales.Endpoints;

/// <summary>
/// Checkout and orders.
///
/// <para>
/// <b>The shape of this surface is the fix for Onyx's single worst endpoint.</b> Onyx had one:
/// <c>POST /api/orders</c>, taking a <c>CreateOrderRequest</c> carrying product ids, a promo
/// code, a number of points to redeem, <i>and the shipping address</i>, whose handler created
/// the order row, then priced it, then deducted stock, then charged the card, committing at
/// each step. There was no way to see a price before committing to buy, no way to retry
/// anything, and a failure at step four left the first three standing (<b>D2</b>, <b>D3</b>,
/// <b>D8</b>).
/// </para>
///
/// <para>
/// Here it is three calls, and the split is the design:
/// </para>
///
/// <list type="number">
/// <item><c>POST /api/checkouts</c> — prices the basket and returns a session id. Nothing is
/// held, nothing is charged, and it can be called as many times as the customer likes.</item>
/// <item><c>GET /api/checkouts/{id}</c> — the quote, with every adjustment and its basis, so
/// "why is my total this?" is answerable before anyone commits to anything.</item>
/// <item><c>POST /api/checkouts/{id}/confirm</c> — holds the stock, the promotion and the
/// points, creates the order, and returns <c>202 Accepted</c> with the order in
/// <c>PaymentPending</c>. Idempotent on the session id.</item>
/// </list>
///
/// <para>
/// <b>202, not 201.</b> A confirmed checkout is an order that exists and is not yet paid. Onyx
/// <b>A9</b> told customers their payment had succeeded before the payment row was added to the
/// context, so a rollback left them with a confirmation for a transaction that never happened.
/// The status code says what is true: we have accepted the order and the money is still pending.
/// </para>
///
/// <para>
/// <b>What has no endpoint.</b> Marking an order paid, cancelling a checkout's holds, granting
/// points — those are module-to-module commands, not things a client calls.
/// <c>MarkOrderPaidCommand</c> in particular is reachable only from a provider's answer, which
/// is <b>M1</b> and <b>M4</b> enforced by the absence of a route. Onyx exposed
/// <c>PUT /api/orders/{id}/status</c> with an unvalidated string, so any authenticated caller
/// could move their own order to <c>Paid</c> (<b>S7</b>).
/// </para>
/// </summary>
public static class SalesEndpoints
{
    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        var checkouts = app.MapGroup("/api/checkouts").WithTags("Checkout");
        var orders = app.MapGroup("/api/orders").WithTags("Orders");

        checkouts.MapPost("/", async (
            StartCheckoutRequest request,
            ICurrentActor actor,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            // The customer is the authenticated principal, never a field on the request. Onyx
            // S4 read the id from the body on every endpoint and compared it to nothing, so any
            // customer could check out as any other.
            if (actor.CustomerId is not { } customerId)
            {
                return Results.Unauthorized();
            }

            var result = await sender.Send(
                new StartCheckoutCommand(
                    customerId,
                    request.CountryCode,
                    request.Lines,
                    request.PromotionCode,
                    request.PointsToRedeem),
                cancellationToken);

            return result.Match(
                sessionId => Results.Created($"/api/checkouts/{sessionId}", new { sessionId }),
                ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("StartCheckout")
        .WithSummary("Prices a basket and opens a checkout session. Holds nothing.")
        .WithDescription(
            "**No money value crosses the wire inbound.** The request carries product ids, "
            + "quantities, a country, an optional promotion code and an optional number of "
            + "points. Every price comes from Catalog inside the handler, every discount from "
            + "the module that owns it, and the total from one pure function. That is Onyx "
            + "**M2** — \"the client never sends a money value\" — and it is why the amount "
            + "charged can be derived from the server-held session rather than trusted from a "
            + "request.\n\n"
            + "The session is the anchor of the whole flow. It commits **first**, before any "
            + "hold is placed anywhere, so every hold in every module references something the "
            + "expiry sweeper can enumerate. If the session were created at confirm time, a "
            + "confirm failure would leave stock held against an id that exists nowhere — "
            + "invisible, permanent loss of sellable inventory.\n\n"
            + "The session lives 20 minutes. Calling this again produces a new session; nothing "
            + "is held, so there is nothing to clean up.")
        .WithWriteProblems();

        checkouts.MapGet("/{checkoutSessionId:guid}", async (
            Guid checkoutSessionId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(
                new GetCheckoutQuoteQuery(checkoutSessionId), cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("GetCheckoutQuote")
        .WithSummary("The quote, with every step of the pricing shown.")
        .WithDescription(
            "**The `adjustments` array is the point of this response.** Onyx stored one "
            + "`decimal Total` computed by a single expression with four interleaved rules, so "
            + "nothing could say which rule produced which part of it — **M19**.\n\n"
            + "Here each adjustment carries its kind, a description, a signed amount and the "
            + "**basis** it was computed from, so the total is reconstructible by adding the "
            + "rows up and every figure can be traced to the rule that produced it:\n\n"
            + "| step | field | Onyx issue it closes |\n"
            + "|---|---|---|\n"
            + "| merchandise | `merchandiseSubtotal` | — |\n"
            + "| promotion discount | `adjustments[PromotionDiscount]` | M8 |\n"
            + "| loyalty discount | `adjustments[LoyaltyDiscount]` | M11 |\n"
            + "| shipping | `shipping` | M9 |\n"
            + "| tax | `tax` on `taxableBase` | M10 |\n\n"
            + "`expiresAtUtc` is when the quote stops being honoured. Re-reading this endpoint "
            + "does not extend it.")
        .Produces<CheckoutQuoteResponse>()
        .WithReadProblems();

        checkouts.MapPost("/{checkoutSessionId:guid}/confirm", async (
            Guid checkoutSessionId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(
                new ConfirmCheckoutCommand(checkoutSessionId), cancellationToken);

            return result.Match(
                confirmed => Results.Accepted($"/api/orders/{confirmed.OrderId}", confirmed),
                ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("ConfirmCheckout")
        .WithSummary("Holds the stock, the promotion and the points, and creates the order.")
        .WithDescription(
            "Returns **202 Accepted**, not 201: the order exists and the money does not yet. "
            + "Onyx **A9** dispatched its payment-succeeded notification before the payment row "
            + "was even added to the context.\n\n"
            + "**Idempotent on the session id.** Calling it twice returns the same order rather "
            + "than placing a second one — the unique index on `Orders.CheckoutSessionId` is "
            + "what guarantees it, not the handler's check, so two concurrent confirms cannot "
            + "both win. Onyx **M3**/**M5**: three payment attempts with no idempotency key at "
            + "all, so a gateway timeout followed by a retry could charge twice.\n\n"
            + "It spans two transactions in other modules and one here, and **no invariant "
            + "crosses the boundary** — stock correctness stays Catalog's. What makes that safe "
            + "is four layers of recovery, cheapest first, not a distributed transaction:\n\n"
            + "0. **Retry.** Every hold is idempotent on the session id, so resending the same "
            + "confirm is safe and free. Covers most transient failures.\n"
            + "1. **Inline compensation.** A failure mid-flight releases what it already held. "
            + "Best-effort and never relied on — it can fail for the same reason the step it is "
            + "compensating for failed.\n"
            + "2. **The expiry sweeper.** `sales.checkout-expiry` releases any session that died "
            + "without resolving. **This is the actual guarantee**, because it does not require "
            + "the failing request to survive long enough to clean up after itself.\n"
            + "3. **Orphan reconciliation.** `sales.orphan-reconciliation` finds stock held "
            + "against a session that does not exist, which layer 2 structurally cannot see. It "
            + "covers bugs rather than failures, so it should find nothing and alerts when it "
            + "does.\n\n"
            + "Beside them, `sales.integrity-check` re-derives every total from its rows and "
            + "reports mismatches without correcting them.\n\n"
            + "409 if the session expired, was already cancelled, or its stock went while the "
            + "customer was deciding.")
        .Produces<ConfirmedCheckoutResponse>(StatusCodes.Status202Accepted)
        .WithWriteProblems();

        checkouts.MapDelete("/{checkoutSessionId:guid}", async (
            Guid checkoutSessionId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new CancelCheckoutCommand(checkoutSessionId), cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("CancelCheckout")
        .WithSummary("Abandons a checkout, releasing anything it held.")
        .WithDescription(
            "The customer-driven half of recovery layer 2. Cancelling raises the same "
            + "`CheckoutAbandoned` event the expiry sweeper raises, because the consumers' "
            + "question is identical in both cases: release whatever you were holding for this "
            + "session.\n\n"
            + "Not required for correctness — the sweeper would get there within 20 minutes — "
            + "but it returns the stock to the shelf immediately, which matters for a product "
            + "with one left.")
        .WithWriteProblems();

        orders.MapGet("/{orderId:guid}", async (
            Guid orderId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(new GetOrderQuery(orderId), cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("GetOrder")
        .WithSummary("One order, with its items, its adjustments and its lifecycle timestamps.")
        .WithDescription(
            "The adjustments are **copied** onto the order at the sale rather than referenced, "
            + "so a receipt reprinted in two years does not depend on a checkout session row "
            + "still existing — and the sessions are swept. The duplication is deliberate and "
            + "one-directional: nothing ever writes back.\n\n"
            + "`quantityShipped` per item supports partial shipment, which Onyx's single "
            + "order-level status string could not represent.")
        .Produces<OrderDetailResponse>()
        .WithReadProblems();

        orders.MapGet("/", async (
            ICurrentActor actor,
            ISender sender,
            CancellationToken cancellationToken,
            [FromQuery] string? cursor = null,
            [FromQuery] int limit = 20) =>
        {
            if (actor.CustomerId is not { } customerId)
            {
                return Results.Unauthorized();
            }

            var result = await sender.Send(
                new GetCustomerOrdersQuery(customerId, cursor, limit), cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("GetMyOrders")
        .WithSummary("The caller's own orders, newest first. Keyset paged.")
        .WithDescription(
            "Scoped to the authenticated customer by the token, not by a query parameter — "
            + "there is no `customerId` to tamper with. Onyx's `GET /api/orders/search` took "
            + "one and compared it to nothing (**S4**), did `.Skip().Take()` with no `ORDER BY` "
            + "at all (**A27**), and left `take` unvalidated so a caller could ask for every "
            + "order ever placed (**A28**).\n\n"
            + "`cursor` is the previous page's last `placedAtUtc`. `limit` is capped at 100. "
            + "`itemCount` is counted by the database rather than by a query per row (**A26**).")
        .Produces<OrderPage>()
        .WithReadProblems();

        orders.MapPost("/{orderId:guid}/cancel", async (
            Guid orderId,
            CancelOrderRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new CancelOrderCommand(orderId, request.Reason), cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("CancelOrder")
        .WithSummary("Cancels an order and releases everything it held.")
        .WithDescription(
            "**Administrator only**, and it is a `POST` to a named action rather than a status "
            + "field a caller can set. Onyx exposed `PUT /api/orders/{id}/status` taking an "
            + "unvalidated string with no ownership check, so any authenticated caller could "
            + "move their own order to `Paid` (**S7**, **M19**).\n\n"
            + "What a cancellation is allowed to do is decided by `Order.Cancel`, not by this "
            + "route: a shipped order cannot be cancelled, and a cancelled one cannot be "
            + "cancelled twice. The stock, the promotion use and the points come back through "
            + "the outbox after this commits — guaranteed rather than fast.")
        .WithWriteProblems();

        orders.MapPost("/{orderId:guid}/refund", async (
            Guid orderId,
            RefundOrderRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new RefundOrderCommand(orderId, request.RefundReference), cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("RefundOrder")
        .WithSummary("Records a refund against a paid order and reverses its points.")
        .WithDescription(
            "`refundReference` is the provider's, and it is required — Sales never decides that "
            + "money moved. **M4**: the reference is set only from a provider response.\n\n"
            + "Reversing the loyalty points is Loyalty's own subscription to the event this "
            + "raises, bounded by what the earning actually gave and clamped to the current "
            + "balance. `ISSUES.md` §7 lists reversal on refund as a capability Onyx did not "
            + "have, and **M12** names it.")
        .WithWriteProblems();

        orders.MapPost("/{orderId:guid}/ship", async (
            Guid orderId,
            ShipOrderRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new ShipOrderCommand(orderId, request.Lines), cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("ShipOrder")
        .WithSummary("Ships some or all of an order's items. Supports partial shipment.")
        .WithDescription(
            "Per-item quantities, so a two-line order can go out in two parcels and the second "
            + "line still reads as outstanding. The order reaches `Shipped` only when every "
            + "item is fully shipped — the status is derived from the items rather than set "
            + "beside them, so it cannot disagree with them.\n\n"
            + "Onyx carried one order-level status string, which is why partial shipment could "
            + "not be represented at all and why **M19** records the status as a field anyone "
            + "could set to anything.")
        .WithWriteProblems();

        orders.MapPost("/{orderId:guid}/deliver", async (
            Guid orderId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new MarkOrderDeliveredCommand(orderId), cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("MarkOrderDelivered")
        .WithSummary("Marks a fully shipped order delivered.")
        .WithDescription(
            "Only a `Shipped` order can be delivered, and only one with every item shipped can "
            + "be `Shipped` — so the sequence is enforced by the aggregate at each step rather "
            + "than assumed by the caller.")
        .WithWriteProblems();

        return app;
    }

    private static RouteHandlerBuilder WithWriteProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static RouteHandlerBuilder WithReadProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static IResult ToProblem(List<Error> errors)
    {
        var first = errors[0];

        var statusCode = first.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Problem(
            title: first.Code,
            detail: first.Description,
            statusCode: statusCode,
            extensions: errors.Count > 1
                ? new Dictionary<string, object?>
                {
                    ["errors"] = errors.Select(error => new { error.Code, error.Description }).ToArray()
                }
                : null);
    }
}

/// <summary>
/// A basket, a country, and optionally a promotion code and some points.
///
/// <para>
/// Note what is absent: any money value, and any customer id. The prices come from Catalog and
/// the customer comes from the token.
/// </para>
/// </summary>
public sealed record StartCheckoutRequest(
    string CountryCode,
    IReadOnlyList<CheckoutLineRequest> Lines,
    string? PromotionCode = null,
    int PointsToRedeem = 0);

public sealed record CancelOrderRequest(string Reason);

/// <summary>The provider's refund reference. Required — Sales never decides money moved.</summary>
public sealed record RefundOrderRequest(string RefundReference);

public sealed record ShipOrderRequest(IReadOnlyList<ShipmentLine> Lines);
