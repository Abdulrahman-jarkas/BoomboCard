using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Contracts;

/// <summary>
/// One product and a quantity. Several lines for the same product in one request are summed
/// before anything is applied, because the idempotency key is per product and reference.
/// </summary>
public sealed record StockLine(Guid ProductId, int Quantity);

/// <summary>
/// Takes stock off the shelf for a checkout.
///
/// A <b>command</b>, not an event, because the caller needs the answer to decide what to do
/// next: "is it in stock" is the most important question at checkout and out-of-stock is a
/// normal business outcome, not an exception. Choreography — Catalog listening for
/// CheckoutSessionCreated — would turn an instant 409 into a wait for a second asynchronous
/// event.
///
/// <para>
/// <b>All lines in one command.</b> Catalog loads every product, deducts each, and saves once,
/// so a three-line order cannot half-succeed. Never loop this per line: that is Onyx D2, where
/// SaveChangesAsync sat inside the per-line loop and a short line partway through left the
/// earlier deductions committed.
/// </para>
///
/// <para>
/// <b>Idempotent on <paramref name="Reference"/></b> (the checkout session id). A retry of the
/// same checkout is safe and is the cheapest recovery path when a later step fails.
/// </para>
/// </summary>
public sealed record DeductStockCommand(string Reference, IReadOnlyList<StockLine> Lines)
    : IRequest<ErrorOr<Success>>, ICatalogRequest;

/// <summary>
/// Puts stock back after a checkout did not complete.
///
/// Issued from an integration event rather than a direct call — nobody is waiting, it cannot
/// fail for a business reason, and it must be guaranteed rather than fast. The three reasons it
/// fires are payment failure, session expiry and order cancellation.
///
/// Session expiry is the one that bounds the system's exposure: without it, a checkout that
/// neither succeeds nor fails strands stock forever.
/// </summary>
public sealed record RestoreStockCommand(
    string Reference,
    RestoreReason Reason,
    IReadOnlyList<StockLine> Lines) : IRequest<ErrorOr<Success>>, ICatalogRequest;

/// <summary>
/// The subset of stock reasons another context is allowed to name. Catalog's internal
/// StockReason has more cases (Receipt, Shrinkage, Adjustment) that are nobody else's business.
/// </summary>
public enum RestoreReason
{
    PaymentFailed = 1,
    SessionExpired = 2,
    OrderCancelled = 3,
    Return = 4
}

/// <summary>
/// "Can we cover these quantities?" — the admin bulk-reorder screen, and anything that wants to
/// check availability without taking stock.
///
/// Onyx M18: its equivalent used <c>available &lt;= requested</c>, so a product with EXACTLY
/// enough stock was reported short and staff raised supplier orders they did not need. The order
/// path used the correct <c>&lt;</c>, so the two screens disagreed. Here there is one rule,
/// <c>StockLevel.CanFulfil</c>, and a boundary test proving 3-of-3 succeeds.
///
/// A query, not a command: it takes no transaction and changes nothing.
/// </summary>
public sealed record BulkStockCheckQuery(IReadOnlyList<StockLine> Lines)
    : IRequest<ErrorOr<List<StockCheckResult>>>;

/// <summary>
/// Whether one product can cover a requested quantity.
///
/// <c>CanFulfil</c> uses the single availability rule, <c>available &gt;= requested</c>, so
/// exactly enough IS enough. A product that does not exist returns <c>Exists: false</c> rather
/// than reading as plentiful.
/// </summary>
public sealed record StockCheckResult(
    Guid ProductId,
    string Sku,
    int Requested,
    int Available,
    bool CanFulfil,
    bool Exists,
    bool IsActive);
