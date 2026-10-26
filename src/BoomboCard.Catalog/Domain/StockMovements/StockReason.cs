namespace BoomboCard.Catalog.Domain.StockMovements;

/// <summary>
/// Why stock moved. Required on every movement, because the reason is what makes the audit
/// trail usable — and because Onyx's replenishment job (D5) moved stock for no stated reason
/// at all, which is how it managed to invent inventory unnoticed.
///
/// Deduction reasons are negative movements; the rest are positive.
/// </summary>
public enum StockReason
{
    /// <summary>Deducted at checkout, referencing the checkout session.</summary>
    Sale = 1,

    /// <summary>Restored because payment did not succeed.</summary>
    PaymentFailed = 2,

    /// <summary>Restored because the checkout session expired without resolving.</summary>
    SessionExpired = 3,

    /// <summary>Restored because the order was cancelled after payment.</summary>
    OrderCancelled = 4,

    /// <summary>Restored because the customer returned goods.</summary>
    Return = 5,

    /// <summary>Added by goods-in against a real delivery.</summary>
    Receipt = 6,

    /// <summary>Deliberate correction after a stocktake.</summary>
    Adjustment = 7,

    /// <summary>Loss, breakage or theft.</summary>
    Shrinkage = 8
}

/// <summary>
/// Well-known movement references. Shared so the single-product create path and the CSV import
/// path agree — the reference is the idempotency key, so two spellings of the same thing would
/// defeat the unique index on (ProductId, Reference, Reason).
/// </summary>
public static class StockReferences
{
    public const string OpeningBalance = "OPENING-BALANCE";
}
