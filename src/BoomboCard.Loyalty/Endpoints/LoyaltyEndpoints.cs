using BoomboCard.Customers.Contracts.Authorization;
using BoomboCard.Loyalty.Contracts;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BoomboCard.Loyalty.Endpoints;

/// <summary>
/// A customer's own points, plus the administrative adjustment.
///
/// <para>
/// Routes are customer-scoped and carry <c>customerId</c>, so the <c>SelfOrAdmin</c> policy
/// applies the ownership check — Onyx <b>S4</b> took an id from the route and never compared it
/// to the authenticated principal, on every endpoint.
/// </para>
///
/// <para>
/// Note which operations have <b>no</b> endpoint: granting, redeeming, releasing and reversing.
/// Those are module-to-module commands the checkout and refund flows send, not things a client
/// calls — exposing "credit me some points" over HTTP would be the same category of mistake as
/// Onyx <b>M2</b>, where the client supplied the amount to charge.
/// </para>
/// </summary>
public static class LoyaltyEndpoints
{
    public static IEndpointRouteBuilder MapLoyaltyEndpoints(this IEndpointRouteBuilder app)
    {
        var points = app.MapGroup("/api/customers/{customerId:guid}/points").WithTags("Loyalty");

        points.MapGet("/", async (
            Guid customerId,
            ISender sender,
            CancellationToken cancellationToken,
            [FromQuery] string currencyCode = "USD") =>
        {
            var result = await sender.Send(
                new GetPointsBalanceQuery(customerId, currencyCode), cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .WithName("GetPointsBalance")
        .WithSummary("The balance, and what it is worth in money.")
        .WithDescription(
            "`value` is the point of this response. Onyx **M11** is a Critical finding whose "
            + "whole substance is that points had no monetary value at all — its order endpoint "
            + "accepted `RedeemPoints`, priced the order without reference to it, saved the "
            + "total, and only then subtracted the points. They bought nothing.\n\n"
            + "100 points to one unit of currency, so one point is exactly one cent — a rate "
            + "chosen so the conversion can never round. A rate like \"3 points per unit\" makes "
            + "100 points worth 33.333…, and that fraction then has to be rounded somewhere, in "
            + "a direction somebody has to decide, on a number the customer can check.")
        .Produces<PointsBalanceResponse>()
        .WithReadProblems();

        points.MapGet("/statement", async (
            Guid customerId,
            ISender sender,
            CancellationToken cancellationToken,
            [FromQuery] string? cursor = null,
            [FromQuery] int limit = 50) =>
        {
            var result = await sender.Send(
                new GetPointsStatementQuery(customerId, cursor, limit), cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .WithName("GetPointsStatement")
        .WithSummary("Every movement on the account, newest first. Keyset paged.")
        .WithDescription(
            "`ISSUES.md` §7 lists a points statement as a capability that does not exist, and "
            + "Onyx could not have built one: the balance was a single mutable `int` with no "
            + "history, so there was nothing to list. **M11** records the consequence — \"there "
            + "is no way to answer 'where did my points go?'\".\n\n"
            + "Every row names its reason (`Earned`, `Redeemed`, `Returned`, `Expired`, "
            + "`Reversed`, `Adjusted`, `Forfeited`) and the thing that caused it. Nothing is "
            + "ever deleted or updated: expiry writes a negative row rather than removing the "
            + "earning it lapses, so the balance can be reconstructed by replaying the log — and "
            + "`loyalty.points-integrity-check` asserts that it does.\n\n"
            + "`cursor` is the previous page's last `occurredAtUtc`. `limit` is capped at 200.")
        .Produces<PointsStatementResponse>()
        .WithReadProblems();

        points.MapPost("/validate-redemption", async (
            Guid customerId,
            ValidateRedemptionRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(
                new ValidatePointsRedemptionQuery(customerId, request.Points, request.CurrencyCode),
                cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .WithName("ValidatePointsRedemption")
        .WithSummary("What redeeming these points would be worth, without spending them.")
        .WithDescription(
            "**This endpoint is the D4 fix.** Onyx checked balance sufficiency inside the debit, "
            + "which ran *after* the order was committed — so an insufficient balance threw an "
            + "`InvalidOperationException` that surfaced as a 500 while the order and every "
            + "stock decrement stood. Validation happens here, before anything is written.\n\n"
            + "A query: it changes nothing and spends nothing, so a basket page can call it on "
            + "every change. The actual debit is a module-to-module command that commits in the "
            + "same transaction as the order.\n\n"
            + "Both use the same rule — `PointsAccount.CanRedeem` — so a quote cannot promise a "
            + "discount the debit then refuses. Onyx **M18** is the warning about writing one "
            + "rule twice.")
        .Produces<PointsDiscountResponse>()
        .WithReadProblems();

        points.MapPost("/adjustments", async (
            Guid customerId,
            AdjustPointsRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new AdjustPointsCommand(customerId, request.Points, request.Note),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("AdjustPoints")
        .WithSummary("A manual correction or goodwill grant. Administrator only.")
        .WithDescription(
            "`points` is signed, so this covers both a credit and a correction. The actor is "
            + "taken from the authenticated principal and recorded on the ledger entry — never "
            + "from the request body, because an actor a caller supplies is one a caller can "
            + "forge, and the only reason to record one is to be able to trust it afterwards.\n\n"
            + "Onyx had no audit trail of any kind (**A40**), and its one loyalty write path "
            + "recorded neither the actor nor the movement.")
        .Produces(StatusCodes.Status204NoContent)
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

/// <summary>How many points to price, and in what currency.</summary>
public sealed record ValidateRedemptionRequest(int Points, string CurrencyCode);

/// <summary>
/// A signed adjustment and the reason for it. The administrator making it is read from the
/// token, not from here.
/// </summary>
public sealed record AdjustPointsRequest(int Points, string Note);
