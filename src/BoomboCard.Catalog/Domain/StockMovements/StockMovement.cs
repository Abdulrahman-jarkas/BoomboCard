using BoomboCard.SharedKernel;

namespace BoomboCard.Catalog.Domain.StockMovements;

/// <summary>
/// An immutable record of one change to one product's stock.
///
/// The counter on Product is the state; these are the log. <c>SUM(Delta)</c> for a product must
/// equal its current quantity, which is an invariant a reconciliation job can assert — the
/// check Onyx never had, which is why its replenishment job could heal every oversell before
/// anyone noticed (D1 concealed by D5).
///
/// Its own entity with its own table rather than a collection on Product: the row count grows
/// without bound, and loading a product's entire movement history to deduct one unit would be
/// exactly the read-modify-write pattern we are avoiding.
///
/// <para>
/// <b>Idempotency.</b> <see cref="Reference"/> plus <see cref="Reason"/> plus the product is
/// unique (see StockMovementConfiguration). That is what makes at-least-once outbox delivery
/// safe: a redelivered "restore stock for session X" finds the movement already present and
/// no-ops instead of inflating stock. Onyx's payment retry had no idempotency key at all (M5),
/// so a timeout followed by a retry could charge twice.
/// </para>
/// </summary>
public sealed class StockMovement : Entity
{
    public Guid ProductId { get; private init; }

    /// <summary>Signed: negative for a deduction, positive for a restore or receipt.</summary>
    public int Delta { get; private init; }

    public StockReason Reason { get; private init; }

    /// <summary>
    /// What caused the movement, and the idempotency key: a checkout session id, an order id,
    /// a purchase order number, or the user who made a manual adjustment.
    /// </summary>
    public string Reference { get; private init; } = string.Empty;

    /// <summary>The resulting quantity, so the log can be audited without replaying it.</summary>
    public int QuantityAfter { get; private init; }

    public DateTimeOffset OccurredAtUtc { get; private init; }

    private StockMovement() { }

    internal static StockMovement Create(
        Guid productId,
        int delta,
        StockReason reason,
        string reference,
        int quantityAfter,
        DateTimeOffset occurredAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            Delta = delta,
            Reason = reason,
            Reference = reference,
            QuantityAfter = quantityAfter,
            OccurredAtUtc = occurredAtUtc
        };

    public bool IsDeduction => Delta < 0;
}
