using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Contracts.Authorization;
using BoomboCard.Payments.Application.Payments.Commands;
using BoomboCard.Payments.Application.Payments.Queries;
using BoomboCard.Payments.Domain.Gateway;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BoomboCard.Payments.Endpoints;

/// <summary>
/// Paying for an order, and finding out what happened.
///
/// <para>
/// <b>Onyx's entire payment surface was one action</b>, <c>PaymentsController.Submit</c>, which
/// opened a transaction, looped a gateway call three times inside it swallowing every exception,
/// never inspected the result, wrote <c>Status = "Paid"</c> unconditionally, and returned a
/// success response with a locally generated confirmation code. Nine findings in one method, four
/// of them Critical.
/// </para>
///
/// <para>
/// Here it is two calls, and the split is the same reasoning as checkout's:
/// </para>
///
/// <list type="number">
/// <item><c>POST /api/orders/{orderId}/payments</c> — creates the intent. Fixes the amount from
/// the order, generates the idempotency key, contacts nobody.</item>
/// <item><c>POST /api/payments/{paymentId}/submit</c> — sends it to the provider with a token,
/// outside every transaction, and returns the real outcome.</item>
/// </list>
///
/// <para>
/// <b>A decline is a 200, not a 500.</b> The response carries <c>status</c>,
/// <c>declineCode</c> and <c>canRetry</c>, because a declined card is a normal business outcome
/// and the customer needs to know which of "try another card" and "call your bank" applies. Onyx
/// had no declined path at all — the register's note is that its gateway host did not resolve, so
/// <i>every payment it ever took</i> was a failure recorded as a success.
/// </para>
///
/// <para>
/// <b>What has no endpoint.</b> Capturing, cancelling an authorization, and applying an outcome.
/// Those are driven by the provider's answer or by a background job — never by a caller. There is
/// deliberately no route that can move a payment to a settled status, which is <b>M1</b> and
/// <b>M4</b> enforced by absence rather than by validation.
/// </para>
/// </summary>
public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/api/payments").WithTags("Payments");
        var orders = app.MapGroup("/api/orders").WithTags("Payments");

        orders.MapPost("/{orderId:guid}/payments", async (
            Guid orderId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new InitiatePaymentCommand(orderId), cancellationToken);

            return result.Match(
                initiated => Results.Created($"/api/payments/{initiated.PaymentId}", initiated),
                ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("InitiatePayment")
        .WithSummary("Creates a payment intent for an order. Contacts no provider.")
        .WithDescription(
            "**The request carries an order id and nothing else.** No amount, no currency, no "
            + "card — the figure comes from the order inside the handler, which is Onyx **M2** "
            + "(\"the client never sends a money value\") on the payment path.\n\n"
            + "Separating this from the submission is Onyx **A12**: that controller opened a "
            + "transaction, made up to three gateway calls inside it at the default 100-second "
            + "`HttpClient` timeout, then committed. On its single-writer database that was up to "
            + "five minutes of total write unavailability per payment — one slow provider was a "
            + "complete outage.\n\n"
            + "It also makes the idempotency key durable **before** the first attempt. A key "
            + "generated in memory and lost to a crash is not an idempotency key, and the retry "
            + "that follows is **M5** again.\n\n"
            + "409 if the order already has a payment that can take money. A declined one does "
            + "not count — the customer must be able to try another card.")
        .Produces<InitiatedPaymentResponse>(StatusCodes.Status201Created)
        .WithWriteProblems();

        payments.MapPost("/{paymentId:guid}/submit", async (
            Guid paymentId,
            SubmitPaymentRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(
                new SubmitPaymentCommand(
                    paymentId,
                    new PaymentInstrument(request.Token, request.Last4, request.Brand)),
                cancellationToken);

            // 200 whatever the provider said. A decline is an answer, not a server error.
            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("SubmitPayment")
        .WithSummary("Sends the payment to the provider and returns the real outcome.")
        .WithDescription(
            "**`token` is a provider token, never card data.** The client tokenises the card "
            + "against the provider directly; raw card numbers never touch this system, which is "
            + "the only version of this that keeps the whole codebase out of PCI-DSS scope. There "
            + "is deliberately no `cardNumber` field for anyone to be tempted by.\n\n"
            + "**A decline returns 200** with `status: \"Declined\"`, a `declineCode` and "
            + "`canRetry: true`. Onyx **M1**: a declined card, a provider outage, a DNS failure "
            + "and three timeouts all produced a payment row reading `\"Paid\"`, an order marked "
            + "paid, and a success response — because the result was never inspected.\n\n"
            + "**Four outcomes, not two.** Onyx's gateway result was a `bool Success`, and that "
            + "single boolean is the root of two Critical findings: a `bool` cannot express \"we "
            + "do not know\", so the controller read a lost response as success (**M1**) while "
            + "the retry loop read it as failure and charged again (**M5**). Here:\n\n"
            + "| outcome | `status` | retried? | what it means |\n"
            + "|---|---|---|---|\n"
            + "| succeeded | `Captured` | — | the money moved |\n"
            + "| declined | `Declined` | **never** | the provider's considered answer |\n"
            + "| transport failure | `Failed` | yes, under the same key | the provider never saw it |\n"
            + "| indeterminate | `Failed` | **no** | it may have charged; reconciliation asks |\n\n"
            + "On an indeterminate outcome the response says **do not pay again**. The charge may "
            + "have gone through, and `payments.reconciliation` asks the provider what happened "
            + "to that idempotency key rather than guessing in either direction.\n\n"
            + "Idempotent: submitting an already-answered payment returns its outcome rather than "
            + "charging again.")
        .Produces<SubmittedPaymentResponse>()
        .WithWriteProblems();

        payments.MapGet("/{paymentId:guid}", async (
            Guid paymentId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(new GetPaymentQuery(paymentId), cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("GetPayment")
        .WithSummary("One payment, with every provider interaction and every refund.")
        .WithDescription(
            "**The `attempts` array is Onyx A17.** That finding reads: \"Nothing records that "
            + "three attempts were made, what each returned, or why they failed. When a customer "
            + "disputes a charge there is no history to consult.\" The only trace of a payment "
            + "failure in that system was a `Console.WriteLine` inside an empty catch block "
            + "(**A16**), which no logging pipeline captured.\n\n"
            + "Each row is one thing we asked a provider and one thing it said back: the "
            + "operation, the outcome, the provider's reference and code, the latency, and our "
            + "correlation id so the row joins to the log line. Nothing is ever updated — a "
            + "second attempt is a second row.\n\n"
            + "`providerReference` is null until a provider gives one, and is **never** "
            + "substituted. Onyx **M4** stored a local six-digit confirmation code in that field "
            + "on failure, which made reconciliation guesswork, refunds impossible and "
            + "chargebacks undefendable.\n\n"
            + "`outcomeIsUnknown` means a request was sent and never answered. Such a payment is "
            + "being chased with the provider; it is not a failure.")
        .Produces<PaymentDetailResponse>()
        .WithReadProblems();

        payments.MapGet("/", async (
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
                new GetCustomerPaymentsQuery(customerId, cursor, limit), cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.VerifiedCustomer)
        .WithName("GetMyPayments")
        .WithSummary("The caller's own payments, newest first. Keyset paged.")
        .WithDescription(
            "Scoped to the authenticated customer by the token, not by a query parameter — there "
            + "is no `customerId` to tamper with (**S4**). `cursor` is the previous page's last "
            + "`initiatedAtUtc`; `limit` is capped at 100 (**A27**, **A28**).")
        .Produces<PaymentPage>()
        .WithReadProblems();

        payments.MapPost("/{paymentId:guid}/refunds", async (
            Guid paymentId,
            RefundRequest request,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(
                new RefundPaymentCommand(
                    paymentId, request.Amount, request.Reason, request.IdempotencyKey),
                cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("RefundPayment")
        .WithSummary("Gives money back, in whole or in part.")
        .WithDescription(
            "**Administrator only.** Omit `amount` to refund everything that has not already "
            + "been refunded; supply it for a partial refund.\n\n"
            + "The amount is **capped by what the payment actually captured**, summed from the "
            + "refund rows inside the aggregate rather than trusted from the request. That is the "
            + "same lesson as `BUGS-AND-LESSONS.md` §13 applied to money instead of stock: a "
            + "caller that decides how much it may give back is a caller being trusted with "
            + "somebody else's money.\n\n"
            + "`idempotencyKey` is the **refund's own** key, not the payment's — two refunds "
            + "against one payment are two separate provider transactions and must deduplicate "
            + "independently. Unique at the database on `(paymentId, idempotencyKey)`, so a "
            + "retried request or a double-clicked button refunds once.\n\n"
            + "409 if the payment has no provider reference, which is the state Onyx **M4** made "
            + "indistinguishable from a real payment — and the register lists \"refunds are "
            + "impossible\" as a direct consequence.")
        .Produces<RefundResponse>()
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
/// A provider token, and optionally the display details the provider returned with it.
///
/// <para>
/// Note the absence of a card number, an expiry and a CVV. The client tokenises against the
/// provider directly and sends the token here, so card data never enters this system.
/// </para>
/// </summary>
public sealed record SubmitPaymentRequest(string Token, string? Last4 = null, string? Brand = null);

/// <summary>
/// How much to give back and why. <c>Amount</c> omitted means everything not already refunded.
/// </summary>
public sealed record RefundRequest(decimal? Amount, string Reason, string IdempotencyKey);
