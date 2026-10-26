using BoomboCard.Loyalty.Contracts;
using BoomboCard.Loyalty.Data;
using BoomboCard.Loyalty.Data.Repositories;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Loyalty.Application.Points.Queries;

/*
 * Queries. None implement ILoyaltyRequest, so no transaction is opened for them.
 *
 * Every one projects into an explicit response record. No aggregate is reachable from any of
 * them — the rule that makes Onyx S11 structurally impossible rather than merely avoided.
 */

internal sealed class GetPointsBalanceQueryHandler(LoyaltyDbContext context)
    : IRequestHandler<GetPointsBalanceQuery, ErrorOr<PointsBalanceResponse>>
{
    public async Task<ErrorOr<PointsBalanceResponse>> Handle(
        GetPointsBalanceQuery query,
        CancellationToken cancellationToken)
    {
        var account = await context.Accounts
            .AsNoTracking()
            .Where(candidate => candidate.CustomerId == query.CustomerId)
            .Select(candidate => new { candidate.Balance, candidate.IsClosed })
            .FirstOrDefaultAsync(cancellationToken);

        if (account is null)
        {
            return PointsErrors.AccountNotFound(query.CustomerId);
        }

        // The conversion, from the one place that does it. Onyx M11: its points had no monetary
        // value at all, so a balance of 500 was a number that meant nothing.
        var value = PointsValue.ValueOf(account.Balance, query.CurrencyCode);

        if (value.IsError)
        {
            return value.Errors;
        }

        return new PointsBalanceResponse(
            query.CustomerId,
            account.Balance,
            value.Value.Amount,
            value.Value.CurrencyCode,
            PointsValue.MinimumRedemption,
            !account.IsClosed && PointsValue.IsRedeemable(account.Balance));
    }
}

/// <summary>
/// The <b>D4</b> fix: sufficiency is answered here, during the quote, before anything is
/// written. Onyx checked it inside the debit, which ran after the order was committed.
/// </summary>
internal sealed class ValidatePointsRedemptionQueryHandler(IPointsRepository points)
    : IRequestHandler<ValidatePointsRedemptionQuery, ErrorOr<PointsDiscountResponse>>
{
    public async Task<ErrorOr<PointsDiscountResponse>> Handle(
        ValidatePointsRedemptionQuery query,
        CancellationToken cancellationToken)
    {
        var account = await points.GetByCustomerAsync(query.CustomerId, cancellationToken);

        if (account is null)
        {
            return PointsErrors.AccountNotFound(query.CustomerId);
        }

        // The SAME rule the debit uses, called from both — so a quote cannot promise a discount
        // the debit then refuses. Onyx M18 is the warning about writing one rule twice.
        var allowed = account.CanRedeem(query.Points);

        if (allowed.IsError)
        {
            return allowed.Errors;
        }

        var discount = PointsValue.ValueOf(query.Points, query.CurrencyCode);

        if (discount.IsError)
        {
            return discount.Errors;
        }

        return new PointsDiscountResponse(
            query.Points,
            discount.Value.Amount,
            discount.Value.CurrencyCode,
            account.Balance - query.Points);
    }
}

internal sealed class GetPointsStatementQueryHandler(LoyaltyDbContext context)
    : IRequestHandler<GetPointsStatementQuery, ErrorOr<PointsStatementResponse>>
{
    private const int MaxLimit = 200;

    public async Task<ErrorOr<PointsStatementResponse>> Handle(
        GetPointsStatementQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            return Error.Validation(
                "GetPointsStatement.InvalidLimit",
                $"Limit must be between 1 and {MaxLimit}.");
        }

        var account = await context.Accounts
            .AsNoTracking()
            .Where(candidate => candidate.CustomerId == query.CustomerId)
            .Select(candidate => new { candidate.Id, candidate.Balance })
            .FirstOrDefaultAsync(cancellationToken);

        if (account is null)
        {
            return PointsErrors.AccountNotFound(query.CustomerId);
        }

        var entries = context.Ledger
            .AsNoTracking()
            .Where(entry => entry.AccountId == account.Id);

        // Keyset paged over the timestamp, newest first. Not unique on its own — two movements
        // can share an instant — so the cursor is a point in time and the page boundary is
        // "strictly older than". A tie spanning the boundary would be dropped, so ties are kept
        // together by comparing on the instant alone and accepting that a page may run slightly
        // long rather than lose a row. Onyx A27 used Skip/Take with no ORDER BY at all.
        if (!string.IsNullOrWhiteSpace(query.Cursor)
            && DateTimeOffset.TryParse(
                query.Cursor,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var cursor))
        {
            entries = entries.Where(entry => entry.OccurredAtUtc < cursor);
        }

        var page = await entries
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Take(query.Limit + 1)
            .Select(entry => new PointsStatementLine(
                entry.Delta,
                entry.Reason.ToString(),
                entry.Reference,
                entry.BalanceAfter,
                entry.OccurredAtUtc))
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > query.Limit;
        var lines = hasMore ? page[..query.Limit] : page;

        return new PointsStatementResponse(
            query.CustomerId,
            account.Balance,
            lines,
            hasMore ? lines[^1].OccurredAtUtc.ToString("O") : null);
    }
}
