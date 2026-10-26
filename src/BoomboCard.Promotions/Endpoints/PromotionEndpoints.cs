using BoomboCard.Customers.Contracts.Authorization;
using BoomboCard.Promotions.Application.Promotions.Commands;
using BoomboCard.Promotions.Application.Promotions.Queries;
using BoomboCard.Promotions.Contracts;
using BoomboCard.Promotions.Domain.PromotionAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BoomboCard.Promotions.Endpoints;

/// <summary>
/// Campaign management, plus the one customer-facing route.
///
/// <para>
/// <b>Everything here is Admin-only except code validation.</b> Promotions are merchandising
/// configuration: who can create a 50%-off code is exactly as sensitive as who can change a
/// price. Onyx's promo surface was a single <c>GET /api/admin/orders/promo-audit</c> that took a
/// caller-supplied wildcard straight into <c>EF.Functions.Like</c> from inside a domain service
/// (<b>A11</b>) — so a client could send <c>%</c> and scan every order in the system — and there
/// was no way to create, edit, pause or report on a campaign at all (<b>M16</b>).
/// </para>
/// </summary>
public static class PromotionEndpoints
{
    public static IEndpointRouteBuilder MapPromotionEndpoints(this IEndpointRouteBuilder app)
    {
        var promotions = app.MapGroup("/api/promotions").WithTags("Promotions");

        MapCustomerEndpoints(promotions);
        MapCampaignEndpoints(promotions);

        return app;
    }

    private static void MapCustomerEndpoints(RouteGroupBuilder promotions)
    {
        promotions.MapPost("/validate", async (
            ValidatePromotionRequest request,
            ISender sender,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            // The customer id comes from the token, never the body. A caller-supplied one would
            // let anybody check — and exhaust the per-customer cap check against — somebody
            // else's entitlement.
            var customerId = http.CustomerId();

            if (customerId is null)
            {
                return Results.Unauthorized();
            }

            var result = await sender.Send(
                new ValidatePromotionQuery(
                    request.Code,
                    customerId.Value,
                    request.CurrencyCode,
                    request.MerchandiseSubtotal,
                    request.Lines),
                cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("ValidatePromotion")
        .WithSummary("Check a promotion code against a basket and see what it is worth.")
        .WithDescription(
            "A query: it changes nothing and reserves nothing. A basket page can call it on "
            + "every change without burning the last uses of a limited campaign — only "
            + "confirming the checkout reserves.\n\n"
            + "**Failures name their reason**, which is the opposite of the sign-in endpoint and "
            + "deliberately so. A promotion code is not a credential: the customer typed it, and "
            + "\"your basket is below the minimum\" is the entire value of the response. Onyx "
            + "**M15** returned a 0% discount for an unrecognised code and let the order through "
            + "as though a valid one had been used, so a typo silently became no discount and no "
            + "explanation.\n\n"
            + "Reasons: `CodeNotRecognised` (404), `NotStartedYet`, `Expired`, "
            + "`MinimumSpendNotMet`, `NotApplicableToBasket`, `CurrencyMismatch` (400), "
            + "`UsageCapReached`, `CustomerCapReached` (409).\n\n"
            + "`discountAmount` is already rounded to cash and already capped at the basis, so "
            + "the caller adds it as a line and recomputes nothing.")
        .Produces<PromotionDiscountResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapCampaignEndpoints(RouteGroupBuilder promotions)
    {
        promotions.MapPost("/", async (
            CreatePromotionCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);

            return result.Match(
                promotionId => Results.Created(
                    $"/api/promotions/{promotionId}",
                    new CreatedPromotionResponse(promotionId)),
                ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("CreatePromotion")
        .WithSummary("Create a campaign. It starts as a Draft and cannot be used until activated.")
        .WithDescription(
            "**This endpoint is the answer to Onyx M16.** Its promotions were two entries in a "
            + "`private static readonly Dictionary<string, DateTime>`, so creating a campaign "
            + "meant editing C#, opening a pull request and deploying — and the one it shipped, "
            + "`SUMMER25`, had an expiry date in the past from the moment it was written.\n\n"
            + "`endsAtUtc` is **exclusive**: \"valid through 31 December\" is "
            + "`2027-01-01T00:00:00Z`. Stating the instant rather than a date plus a convention "
            + "is what closes **M17** — Onyx's comment said \"through the end of the listed "
            + "day\" and its code compared against midnight at the start of it, in server local "
            + "time, so the code died a full day early and the cutover moved with the "
            + "deployment's timezone.\n\n"
            + "Supply exactly one of `discountRate` (a fraction, so `0.10` is 10%) or "
            + "`discountAmount` with a `currency`. A fixed-amount campaign is denominated and "
            + "does not apply to a basket in another currency; a percentage applies to any.\n\n"
            + "`productIds` scopes the discount to those products' lines, so \"20% off "
            + "headphones\" discounts the headphones rather than a fifth of the laptop next to "
            + "them. Category scope is not available: `Product` has no category — see "
            + "`ROADMAP.md`.")
        .Produces<CreatedPromotionResponse>(StatusCodes.Status201Created)
        .WithWriteProblems();

        promotions.MapGet("/{promotionId:guid}", async (
            Guid promotionId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(new GetPromotionQuery(promotionId), cancellationToken);
            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("GetPromotion")
        .WithSummary("The full configuration of one campaign.")
        .Produces<PromotionDetailResponse>()
        .WithReadProblems();

        promotions.MapGet("/", async (
            ISender sender,
            CancellationToken cancellationToken,
            [FromQuery] string? search = null,
            [FromQuery] PromotionStatusValue? status = null,
            [FromQuery] string? cursor = null,
            [FromQuery] int limit = 50) =>
        {
            var result = await sender.Send(
                new SearchPromotionsQuery(search, status, cursor, limit),
                cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("SearchPromotions")
        .WithSummary("Search campaigns. Keyset paged.")
        .WithDescription(
            "Keyset paged over code, which is unique and ordered, so no row can be seen twice or "
            + "skipped however much the table changes between requests (**A27**). `limit` is "
            + "capped at 200 (**A28**).\n\n"
            + "`search` is matched against the normalised code and the description with a "
            + "parameterised predicate. Note what it is not: Onyx **A11** handed a "
            + "caller-supplied pattern straight to `EF.Functions.Like` from inside a domain "
            + "service, so a client could send `%` and table-scan every order in the system.")
        .Produces<PromotionPage>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        promotions.MapPut("/{promotionId:guid}", async (
            Guid promotionId,
            PromotionTerms terms,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new ReconfigurePromotionCommand(promotionId, terms),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("ReconfigurePromotion")
        .WithSummary("Replace a draft campaign's terms. Drafts only.")
        .WithDescription(
            "409 on anything that is not a Draft, and that is a business rule rather than "
            + "caution: a live campaign's terms are something customers may already have been "
            + "shown and acted on, and silently changing the minimum spend under them is the "
            + "same class of wrong as Onyx **A6** — showing one price and charging another.\n\n"
            + "To change a live campaign, close it and create its successor. The ledger then "
            + "attributes each redemption to the terms that were actually in force.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        promotions.MapPost("/{promotionId:guid}/activation", async (
            Guid promotionId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new ActivatePromotionCommand(promotionId),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("ActivatePromotion")
        .WithSummary("Take a campaign live.")
        .WithDescription(
            "400 if the validity window has already ended — activating something already over "
            + "would do nothing, and that is exactly what Onyx shipped: `SUMMER25` was live with "
            + "an expiry of 2026-09-01, before the audit that found it, and nothing anywhere "
            + "could have noticed (**M16**).")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        promotions.MapDelete("/{promotionId:guid}/activation", async (
            Guid promotionId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new PausePromotionCommand(promotionId),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("PausePromotion")
        .WithSummary("Stop a campaign accepting uses. Reversible.")
        .WithDescription(
            "A paused code reports as `CodeNotRecognised` to customers rather than as paused: "
            + "internal campaign state is not something a shopper can act on.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        promotions.MapPost("/{promotionId:guid}/closure", async (
            Guid promotionId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new ClosePromotionCommand(promotionId),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("ClosePromotion")
        .WithSummary("End a campaign for good. NOT reversible.")
        .WithDescription(
            "Terminal by design. Reopening an exhausted campaign would let a \"first 100 "
            + "customers\" promotion serve 150, which is the kind of quiet promise-breaking "
            + "that is worse than refusing. A new campaign is a new promotion with a new code "
            + "and its own ledger.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        promotions.MapGet("/{promotionId:guid}/performance", async (
            Guid promotionId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(
                new GetPromotionPerformanceQuery(promotionId),
                cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("GetPromotionPerformance")
        .WithSummary("How a campaign actually performed.")
        .WithDescription(
            "`ISSUES.md` §7 lists campaign performance reporting as a capability that does not "
            + "exist, and Onyx could not have built it: a promo code was a nullable string on "
            + "the order and nothing recorded whether it had been applied, abandoned or "
            + "refunded.\n\n"
            + "`releasedRedemptions` is the number Onyx could never have produced — it is the "
            + "difference between \"100 people used this\" and \"140 tried and 40 abandoned\". "
            + "The ledger keeps released rows rather than deleting them, which is what makes the "
            + "distinction possible.\n\n"
            + "Aggregated in SQL, with the empty case guarded: a campaign nobody has used yet is "
            + "the normal state of one on its first day, and Onyx **A18** is what happens when a "
            + "report assumes otherwise — an unguarded divide by a count of zero that terminated "
            + "the whole worker process.")
        .Produces<PromotionPerformanceResponse>()
        .WithReadProblems();
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// The authenticated customer, read from the token. Duplicated from Customers'
    /// <c>ICurrentActor</c> on purpose: this module has no reason to depend on that one, and the
    /// claim name is a shared constant in <c>Customers.Contracts</c> so the two cannot drift.
    /// </summary>
    private static Guid? CustomerId(this HttpContext http) =>
        Guid.TryParse(
            http.User.FindFirst(BoomboCardClaims.CustomerId)?.Value,
            out var customerId)
            ? customerId
            : null;

    private static RouteHandlerBuilder WithWriteProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static RouteHandlerBuilder WithReadProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

    /// <summary>
    /// One mapper, so every endpoint returns the same shape. Onyx returned 404 from some paths
    /// and 200-with-an-error-body from others (<b>A23</b>).
    /// </summary>
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
/// A basket to check a code against.
///
/// <para>
/// The amounts are server-held values Sales has already captured from Catalog, passed between
/// two server-side modules. <b>M2</b> — "the client never sends a money value" — is about the
/// wire, and it holds: the checkout request carries product ids and quantities only. This route
/// exists so a basket page can preview a code before a session is confirmed.
/// </para>
/// </summary>
public sealed record ValidatePromotionRequest(
    string Code,
    string CurrencyCode,
    decimal MerchandiseSubtotal,
    IReadOnlyList<BasketLine> Lines);

/// <summary>The id of the campaign just created.</summary>
public sealed record CreatedPromotionResponse(Guid PromotionId);
