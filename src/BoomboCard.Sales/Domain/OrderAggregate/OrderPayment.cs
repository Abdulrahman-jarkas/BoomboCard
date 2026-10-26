using BoomboCard.SharedKernel;

namespace BoomboCard.Sales.Domain.OrderAggregate;

/// <summary>
/// One capture a payment provider confirmed against an order.
///
/// <para>
/// <b>Why this is a row rather than a nullable column on the order.</b> The capture reference is
/// the idempotency key for <c>MarkOrderPaidCommand</c>, so it needs a unique index — and a
/// nullable unique column does not work: SQL Server treats two <c>NULL</c>s as equal in a unique
/// index, so exactly one unpaid order could exist. The usual workaround is a filtered index
/// (<c>WHERE [PaymentReference] IS NOT NULL</c>), which is provider-specific SQL written into
/// the module — the thing <c>AddressConfiguration</c> refused to do for the same reason, and
/// which would make <c>AddSalesModule(Action&lt;DbContextOptionsBuilder&gt;)</c> a fiction.
/// </para>
///
/// <para>
/// So the capture becomes a row that either exists or does not, its reference is non-nullable,
/// and a plain unique index gives the guarantee on any provider. It is the same move
/// <c>Customer.DefaultAddressId</c> made: when a nullable flag needs a constraint, the fix is
/// usually to remodel it, not to filter the index.
/// </para>
///
/// <para>
/// <b>Why it matters that the constraint exists at all.</b> The handler checks for an existing
/// capture first, but that check is a read — two redeliveries of the same provider callback can
/// both pass it. The index is what makes the second one fail and be retried, at which point the
/// read sees the committed row. The constraint is the guarantee; the read is the fast path.
/// Onyx <b>M3</b> is "no duplicate-payment protection" and <b>M5</b> is "retries can
/// double-charge", and a read with nothing behind it would close neither.
/// </para>
///
/// <para>
/// A collection rather than a single row, because Phase 5 will record failed and partial
/// captures here too, and because an order with two successful captures is a thing the database
/// should be able to represent in order to be able to report it. <c>Order.MarkPaid</c> allows
/// only one today — the status machine rejects a second — so the collection holds at most one
/// until Payments says otherwise.
/// </para>
/// </summary>
public sealed class OrderPayment : Entity
{
    public Guid OrderId { get; private init; }

    /// <summary>
    /// The provider's reference. Non-nullable and non-empty: the only legitimate source of it is
    /// a provider response, and Onyx <b>M4</b> fabricated one — when the gateway failed it
    /// substituted a locally generated six-digit number, so the reference looked valid and
    /// reconciliation was impossible.
    /// </summary>
    public string Reference { get; private init; } = string.Empty;

    public decimal Amount { get; private init; }

    public string CurrencyCode { get; private init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; private init; }

    private OrderPayment() { }

    internal static OrderPayment Capture(
        Guid orderId,
        string reference,
        Money amount,
        DateTimeOffset capturedAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Reference = reference,
            Amount = amount.Amount,
            CurrencyCode = amount.CurrencyCode,
            CapturedAtUtc = capturedAtUtc
        };
}
