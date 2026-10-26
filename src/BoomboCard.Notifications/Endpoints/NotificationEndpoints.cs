using BoomboCard.Customers.Contracts.Authorization;
using BoomboCard.Notifications.Application.Notifications.Queries;
using BoomboCard.Notifications.Contracts;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BoomboCard.Notifications.Endpoints;

/// <summary>
/// What the system has told people, and what it failed to tell them.
///
/// <para>
/// <b>Every route here is a read, and that is deliberate.</b> There is no endpoint that sends a
/// notification, because nothing should be able to: a notification exists because a business
/// event happened, and a route that could conjure one would be a route that could email a
/// customer about an order that does not exist. The only entry points are the eleven integration
/// event consumers.
/// </para>
///
/// <para>
/// <b>These are also the endpoints that make Onyx A8 unrepeatable.</b> That finding's second
/// sentence — <i>"and nothing records that"</i> — is why nobody noticed for the whole life of the
/// system that no customer had ever received a single message. A system with
/// <c>GET /api/notifications/health</c> answers that question in five seconds.
/// </para>
///
/// <para>
/// Administrator only, all of them. A notification record names a recipient and shows what the
/// system has sent them, and the delivery history of other people's messages is not a
/// customer-facing capability. A customer's own messages are not exposed either — an inbox is
/// their email client's job, and surfacing one here would add a second place that holds the same
/// information with no second use for it.
/// </para>
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var notifications = app.MapGroup("/api/notifications").WithTags("Notifications");

        notifications.MapGet("/health", async (
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(new GetNotificationHealthQuery(), cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("GetNotificationHealth")
        .WithSummary("How many messages are queued, stuck, rejected or lost.")
        .WithDescription(
            "**This endpoint is the answer to the second half of Onyx A8.** That finding reads: "
            + "\"No customer has ever received any communication from this system, **and nothing "
            + "records that**. There is no retry, no dead-letter, no delivery record.\"\n\n"
            + "The missing implementation is the obvious defect and the cheap one — a day's work. "
            + "The missing *record* is why it survived long enough to be written down in an audit: "
            + "a system where this number can be asked is a system where \"we have sent nothing, "
            + "ever\" is a five-second discovery.\n\n"
            + "`deadLettered` is the figure that matters. Each one is a customer who was not told "
            + "something — and for a password reset, a customer who is locked out and does not "
            + "know why. `oldestQueuedAtUtc` is the second: a queue that is not draining looks "
            + "healthy by every other measure right up until it does not.")
        .Produces<NotificationHealthResponse>()
        .WithReadProblems();

        notifications.MapGet("/", async (
            ISender sender,
            CancellationToken cancellationToken,
            [FromQuery] string? status = null,
            [FromQuery] Guid? customerId = null,
            [FromQuery] string? cursor = null,
            [FromQuery] int limit = 25) =>
        {
            var result = await sender.Send(
                new SearchNotificationsQuery(status, customerId, cursor, limit),
                cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("SearchNotifications")
        .WithSummary("Notifications, newest first, filterable by status and customer. Keyset paged.")
        .WithDescription(
            "`status=DeadLettered` is the query a support conversation starts with, and "
            + "`customerId=...` is the one it continues with — \"what have we actually sent this "
            + "person?\"\n\n"
            + "**No message body is returned by any route here.** A verification or reset body "
            + "holds a plaintext token, because the email is the only thing that can deliver one "
            + "— the database stores a hash. An endpoint that returned it would hand a live "
            + "credential to anybody who could read a notification, which is Onyx **S11** with a "
            + "different column: it serialised its `Customer` entity straight to the wire from "
            + "three endpoints, so every response carried `PasswordHash`.\n\n"
            + "`cursor` is the previous page's last `queuedAtUtc`. `limit` is capped at 100 "
            + "(**A27**, **A28**).")
        .Produces<NotificationPage>()
        .WithReadProblems();

        notifications.MapGet("/{notificationId:guid}", async (
            Guid notificationId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(
                new GetNotificationQuery(notificationId), cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("GetNotification")
        .WithSummary("One notification, with every delivery attempt.")
        .WithDescription(
            "**The `attempts` array is the delivery record Onyx A8 says did not exist.** One row "
            + "per interaction with a provider, whatever the outcome, carrying which sender "
            + "handled it, the provider's own message id when it gave one, our correlation id, "
            + "and the latency. Nothing is ever updated — a second attempt is a second row.\n\n"
            + "`sender` is worth reading carefully. \"It was sent\" means something very "
            + "different for the development logging sender than for a real provider, and a "
            + "delivery log that did not distinguish them is how a development default reaches "
            + "production unnoticed.\n\n"
            + "`containsSecret` says whether the body held a token; `bodyRetained` says whether "
            + "it still does. The body itself is never returned, and `notifications.maintenance` "
            + "discards it an hour after delivery — the token's own expiry is the primary control "
            + "and this is defence in depth, because a notifications table is exactly the thing "
            + "that gets copied into a reporting warehouse.\n\n"
            + "`attempts[].detail` is also absent: it holds provider messages and our own "
            + "exception text, which is diagnostic data for an operator reading logs rather than "
            + "something to return over HTTP. Onyx **S10** wrote `feature.Error.ToString()` into "
            + "the response body in every environment.")
        .Produces<NotificationDetailResponse>()
        .WithReadProblems();

        return app;
    }

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
