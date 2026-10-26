using BoomboCard.Payments.Contracts;
using BoomboCard.Payments.Data;
using BoomboCard.Payments.Domain.PaymentAggregate;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Payments.Application.Payments.Queries;

/*
 * Queries. None implement IPaymentsRequest, so none take a transaction. Every one projects into
 * an explicit response record in SQL — no aggregate is reachable from any of them, which is how
 * Onyx S11 (password hashes reachable through the API) stays closed for a module whose rows
 * carry provider references and decline codes.
 */

/// <summary>
/// One provider interaction, as support sees it.
///
/// <para>
/// <b>This response is Onyx A17.</b> "Nothing records that three attempts were made, what each
/// returned, or why they failed. When a customer disputes a charge there is no history to
/// consult." Each row here is one thing we asked a provider and one thing it said back.
/// </para>
///
/// <para>
/// Note what it does <b>not</b> expose: <c>Detail</c>, which holds provider messages and our own
/// exception text. That is diagnostic data for an operator reading logs, not something to return
/// over HTTP — Onyx <b>S10</b> wrote <c>feature.Error.ToString()</c> into the response body in
/// every environment, leaking file paths, class names and SQL.
/// </para>
/// </summary>
public sealed record PaymentAttemptResponse(
    int AttemptNumber,
    string Operation,
    string Outcome,
    string? ProviderReference,
    string? ProviderCode,
    int LatencyMs,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

public sealed record RefundResponseItem(
    decimal Amount,
    string CurrencyCode,
    string ProviderReference,
    string Reason,
    DateTimeOffset OccurredAtUtc);

/// <summary>A payment with its full history. The support view.</summary>
public sealed record PaymentDetailResponse(
    Guid PaymentId,
    string Reference,
    Guid OrderId,
    Guid CustomerId,
    string Status,
    decimal Amount,
    decimal RefundedAmount,
    string CurrencyCode,
    string? ProviderReference,
    string? DeclineCode,
    int SubmissionAttempts,
    bool CanRetry,
    bool OutcomeIsUnknown,
    DateTimeOffset InitiatedAtUtc,
    DateTimeOffset? AuthorizedAtUtc,
    DateTimeOffset? CapturedAtUtc,
    IReadOnlyList<PaymentAttemptResponse> Attempts,
    IReadOnlyList<RefundResponseItem> Refunds);

public sealed record GetPaymentQuery(Guid PaymentId)
    : IRequest<ErrorOr<PaymentDetailResponse>>;

internal sealed class GetPaymentQueryHandler(PaymentsDbContext context)
    : IRequestHandler<GetPaymentQuery, ErrorOr<PaymentDetailResponse>>
{
    public async Task<ErrorOr<PaymentDetailResponse>> Handle(
        GetPaymentQuery query,
        CancellationToken cancellationToken)
    {
        var payment = await context.Payments
            .AsNoTracking()
            .Where(candidate => candidate.Id == query.PaymentId)
            .Select(candidate => new PaymentDetailResponse(
                candidate.Id,
                candidate.Reference.Value,
                candidate.OrderId,
                candidate.CustomerId,
                candidate.Status.Value.ToString(),
                candidate.Amount.Amount,
                candidate.RefundedAmount.Amount,
                candidate.Amount.CurrencyCode,
                candidate.ProviderReference!.Value,
                candidate.DeclineCode,
                candidate.SubmissionAttempts,
                candidate.Status.Value == PaymentStatusValue.Pending
                    || candidate.Status.Value == PaymentStatusValue.Failed,
                candidate.IndeterminateSinceUtc != null,
                candidate.InitiatedAtUtc,
                candidate.AuthorizedAtUtc,
                candidate.CapturedAtUtc,
                candidate.Attempts
                    .OrderBy(attempt => attempt.OccurredAtUtc)
                    .Select(attempt => new PaymentAttemptResponse(
                        attempt.AttemptNumber,
                        attempt.Operation.ToString(),
                        attempt.Outcome.ToString(),
                        attempt.ProviderReference,
                        attempt.ProviderCode,
                        attempt.LatencyMs,
                        attempt.CorrelationId,
                        attempt.OccurredAtUtc))
                    .ToList(),
                candidate.Refunds
                    .OrderBy(refund => refund.OccurredAtUtc)
                    .Select(refund => new RefundResponseItem(
                        refund.Amount.Amount,
                        refund.Amount.CurrencyCode,
                        refund.ProviderReference,
                        refund.Reason,
                        refund.OccurredAtUtc))
                    .ToList()))
            .FirstOrDefaultAsync(cancellationToken);

        return payment is null ? PaymentErrors.NotFound(query.PaymentId) : payment;
    }
}

internal sealed class GetPaymentSummaryQueryHandler(PaymentsDbContext context)
    : IRequestHandler<GetPaymentSummaryQuery, ErrorOr<PaymentSummaryResponse>>
{
    public async Task<ErrorOr<PaymentSummaryResponse>> Handle(
        GetPaymentSummaryQuery query,
        CancellationToken cancellationToken)
    {
        var payment = await context.Payments
            .AsNoTracking()
            .Where(candidate => candidate.Id == query.PaymentId)
            .Select(candidate => new PaymentSummaryResponse(
                candidate.Id,
                candidate.Reference.Value,
                candidate.OrderId,
                candidate.CustomerId,
                candidate.Status.Value.ToString(),
                candidate.Amount.Amount,
                candidate.RefundedAmount.Amount,
                candidate.Amount.CurrencyCode,
                candidate.DeclineCode,
                candidate.InitiatedAtUtc,
                candidate.CapturedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

        return payment is null ? PaymentErrors.NotFound(query.PaymentId) : payment;
    }
}

/// <summary>
/// "Does this order have a payment, and what became of it?" — published so Sales can answer
/// "may this order be cancelled?" without reaching into Payments' tables.
/// </summary>
internal sealed class GetOrderPaymentOutcomeQueryHandler(PaymentsDbContext context)
    : IRequestHandler<GetOrderPaymentOutcomeQuery, ErrorOr<OrderPaymentOutcome>>
{
    public async Task<ErrorOr<OrderPaymentOutcome>> Handle(
        GetOrderPaymentOutcomeQuery query,
        CancellationToken cancellationToken)
    {
        // The newest payment for the order. An order may legitimately have several — a declined
        // card followed by a good one — and the latest is the one that matters.
        var payment = await context.Payments
            .AsNoTracking()
            .Where(candidate => candidate.OrderId == query.OrderId)
            .OrderByDescending(candidate => candidate.InitiatedAtUtc)
            .Select(candidate => new
            {
                Status = candidate.Status.Value
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (payment is null)
        {
            return new OrderPaymentOutcome(query.OrderId, false, false, false, null);
        }

        return new OrderPaymentOutcome(
            query.OrderId,
            true,
            payment.Status is PaymentStatusValue.Captured
                or PaymentStatusValue.PartiallyRefunded
                or PaymentStatusValue.Refunded,
            payment.Status is PaymentStatusValue.Pending or PaymentStatusValue.Authorized,
            payment.Status.ToString());
    }
}

public sealed record PaymentListItem(
    Guid PaymentId,
    string Reference,
    Guid OrderId,
    string Status,
    decimal Amount,
    string CurrencyCode,
    DateTimeOffset InitiatedAtUtc);

public sealed record PaymentPage(IReadOnlyList<PaymentListItem> Items, string? NextCursor);

/// <summary>
/// A customer's own payments, newest first, keyset paged with a capped limit.
///
/// Onyx <b>A27</b> did <c>.Skip().Take()</c> with no <c>ORDER BY</c> and <b>A28</b> left the
/// page size unvalidated.
/// </summary>
public sealed record GetCustomerPaymentsQuery(
    Guid CustomerId,
    string? Cursor = null,
    int Limit = 20) : IRequest<ErrorOr<PaymentPage>>;

internal sealed class GetCustomerPaymentsQueryHandler(PaymentsDbContext context)
    : IRequestHandler<GetCustomerPaymentsQuery, ErrorOr<PaymentPage>>
{
    private const int MaxLimit = 100;

    public async Task<ErrorOr<PaymentPage>> Handle(
        GetCustomerPaymentsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            return Error.Validation(
                "GetCustomerPayments.InvalidLimit",
                $"Limit must be between 1 and {MaxLimit}.");
        }

        var payments = context.Payments
            .AsNoTracking()
            .Where(payment => payment.CustomerId == query.CustomerId);

        if (!string.IsNullOrWhiteSpace(query.Cursor)
            && DateTimeOffset.TryParse(
                query.Cursor,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var cursor))
        {
            payments = payments.Where(payment => payment.InitiatedAtUtc < cursor);
        }

        var page = await payments
            .OrderByDescending(payment => payment.InitiatedAtUtc)
            .Take(query.Limit + 1)
            .Select(payment => new PaymentListItem(
                payment.Id,
                payment.Reference.Value,
                payment.OrderId,
                payment.Status.Value.ToString(),
                payment.Amount.Amount,
                payment.Amount.CurrencyCode,
                payment.InitiatedAtUtc))
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > query.Limit;
        var items = hasMore ? page[..query.Limit] : page;

        return new PaymentPage(items, hasMore ? items[^1].InitiatedAtUtc.ToString("O") : null);
    }
}
