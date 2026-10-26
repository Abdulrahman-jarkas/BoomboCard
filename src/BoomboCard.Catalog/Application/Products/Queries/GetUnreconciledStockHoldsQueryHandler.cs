using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.StockMovements;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Catalog.Application.Products.Queries;

/// <summary>
/// Finds checkout references whose stock was deducted and never restored.
///
/// <para>
/// <b>Computed from the log, not from a flag.</b> There is no "released" column on a movement —
/// a row is immutable once written, which is the whole point of having a log beside the counter.
/// So "still held" is <c>SUM(Delta) &lt; 0</c> across the movements sharing a reference: the
/// sale is negative, any restore is the matching positive, and a reference that nets to zero has
/// been reconciled. That is the same arithmetic the integrity check uses, which means the sweep
/// cannot disagree with the audit.
/// </para>
///
/// <para>
/// Onyx <b>D5</b> is the anti-pattern this is carefully not: its replenishment job compared the
/// counter against nothing and topped stock up to a level it invented, which silently healed
/// every oversell before anyone could see one. This reports a reference and its quantities and
/// decides nothing — the caller asks Sales what the reference means before anything moves.
/// </para>
/// </summary>
internal sealed class GetUnreconciledStockHoldsQueryHandler(CatalogDbContext context)
    : IRequestHandler<GetUnreconciledStockHoldsQuery, ErrorOr<List<UnreconciledStockHold>>>
{
    private const int MaxLimit = 500;

    /// <summary>
    /// The reasons that participate in the netting: a checkout deduction and the three ways it
    /// can be given back. Deliberately excludes <c>Receipt</c>, <c>Adjustment</c>, <c>Return</c>
    /// and <c>Shrinkage</c> — those reference a delivery note or a user, not a session, and
    /// letting a stocktake correction net against a sale would make a real hold look settled.
    /// </summary>
    private static readonly StockReason[] CheckoutReasons =
    [
        StockReason.Sale,
        StockReason.PaymentFailed,
        StockReason.SessionExpired,
        StockReason.OrderCancelled
    ];

    public async Task<ErrorOr<List<UnreconciledStockHold>>> Handle(
        GetUnreconciledStockHoldsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            return Error.Validation(
                "GetUnreconciledStockHolds.InvalidLimit",
                $"Limit must be between 1 and {MaxLimit}.");
        }

        var checkoutMovements = context.Set<StockMovement>()
            .AsNoTracking()
            .Where(movement => CheckoutReasons.Contains(movement.Reason));

        // Two queries, because the limit is a limit on REFERENCES and grouping per product
        // would let one three-line basket consume three slots. The first picks the references,
        // the second fetches their lines.
        var references = await checkoutMovements
            .GroupBy(movement => movement.Reference)
            .Select(group => new
            {
                Reference = group.Key,
                Net = group.Sum(movement => movement.Delta),
                HeldSinceUtc = group.Min(movement => movement.OccurredAtUtc)
            })
            .Where(row => row.Net < 0 && row.HeldSinceUtc <= query.OlderThanUtc)
            // Oldest first, and ordered because it is capped — an unordered Take would starve
            // the oldest holds pass after pass, which are precisely the ones worth finding.
            // BUGS-AND-LESSONS §8; the EF warning for this now throws.
            .OrderBy(row => row.HeldSinceUtc)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);

        if (references.Count == 0)
        {
            return new List<UnreconciledStockHold>();
        }

        var keys = references.Select(row => row.Reference).ToList();

        var lines = await checkoutMovements
            .Where(movement => keys.Contains(movement.Reference))
            .GroupBy(movement => new { movement.Reference, movement.ProductId })
            .Select(group => new
            {
                group.Key.Reference,
                group.Key.ProductId,
                Net = group.Sum(movement => movement.Delta)
            })
            .Where(row => row.Net < 0)
            .ToListAsync(cancellationToken);

        var byReference = lines
            .GroupBy(row => row.Reference)
            .ToDictionary(
                group => group.Key,
                // Flipped positive so the result can be handed straight to RestoreStockCommand.
                group => (IReadOnlyList<StockLine>)group
                    .Select(row => new StockLine(row.ProductId, -row.Net))
                    .ToList());

        return references
            .Where(row => byReference.ContainsKey(row.Reference))
            .Select(row => new UnreconciledStockHold(
                row.Reference,
                row.HeldSinceUtc,
                byReference[row.Reference]))
            .ToList();
    }
}
