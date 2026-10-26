using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Contracts;

/// <summary>
/// Stock that was taken off the shelf for a checkout and never put back.
///
/// <para>
/// <b>Recovery layer 3, Catalog's half.</b> Layer 1 is inline compensation when a confirm fails
/// mid-flight; layer 2 is the expiry sweeper that releases sessions which died without
/// resolving. Both can be missed — a process killed between the deduction committing and the
/// session row committing leaves a hold that layer 2 will never find a session for, because
/// there is no session.
/// </para>
///
/// <para>
/// This query finds those holds. Catalog can see them — a <c>Sale</c> movement with no later
/// restore against the same reference — but it <b>cannot decide what they mean</b>, because the
/// reference is a checkout session id and Sales owns checkout sessions. A hold whose session
/// confirmed is a completed sale and must be left alone; a hold whose session does not exist at
/// all is the orphan. Catalog asking "did this sell?" is the one question it cannot answer about
/// its own data, which is what makes the two-query handshake the right shape rather than an
/// awkward one.
/// </para>
///
/// <para>
/// A query, so no transaction. <paramref name="OlderThanUtc"/> must be comfortably past the
/// checkout lifetime, or the sweep would compete with live checkouts for the stock they are
/// legitimately holding.
/// </para>
/// </summary>
public sealed record GetUnreconciledStockHoldsQuery(
    DateTimeOffset OlderThanUtc,
    int Limit = 100) : IRequest<ErrorOr<List<UnreconciledStockHold>>>;

/// <summary>
/// One checkout reference and the stock still held against it, aggregated per product.
///
/// <para>
/// <c>Lines</c> are summed deltas flipped positive, so they can be handed straight back to
/// <see cref="RestoreStockCommand"/> — which is idempotent on the same reference, so the sweep
/// is safe to run while a release for the same session is already in flight.
/// </para>
/// </summary>
public sealed record UnreconciledStockHold(
    string Reference,
    DateTimeOffset HeldSinceUtc,
    IReadOnlyList<StockLine> Lines);
