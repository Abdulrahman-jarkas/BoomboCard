using BoomboCard.SharedKernel;

namespace BoomboCard.Payments.Domain.PaymentAggregate;

/// <summary>
/// A row that exists exactly while an order has a payment capable of taking money.
/// <b>Its primary key is the order id</b>, and that is the whole mechanism.
///
/// <para>
/// <b>Onyx M3:</b> "Nothing checks whether the order is already paid, or whether a payment for it
/// exists. The same order can be paid repeatedly, by any authenticated user, accumulating
/// multiple <c>Paid</c> rows that bear no relation to each other or to the total." The obligation
/// line is explicit that application logic is not enough: <i>"Enforced by a unique constraint,
/// not only by application logic."</i>
/// </para>
///
/// <para>
/// <b>Why not a unique index on <c>Payments.OrderId</c>.</b> Because that would be wrong. A
/// declined card must be followed by a second attempt with a different card, so an order
/// legitimately accumulates several payments over its life. What must never exist is <i>two
/// payments both able to take money</i> — which is a predicate over the status, not over the
/// order.
/// </para>
///
/// <para>
/// <b>Why not a filtered unique index</b> — <c>WHERE Status IN ('Pending','Authorized','Captured')</c>.
/// Because that is provider-specific SQL written into a module whose entire seam is
/// <c>AddPaymentsModule(Action&lt;DbContextOptionsBuilder&gt;)</c>. `AddressConfiguration`
/// refused the same shortcut in Phase 1 and `OrderPayment` refused it again in Phase 4.
/// </para>
///
/// <para>
/// So the predicate becomes a row's existence. Initiating a payment inserts the claim and the
/// primary key rejects a second one; reaching a dead end — declined, cancelled, or failed with
/// the attempt budget spent — deletes it, which is what lets the customer try another card. A
/// captured payment keeps its claim forever, so a paid order can never be paid again.
/// </para>
///
/// <para>
/// <b>Third time this project has made this move</b>, after <c>Customer.DefaultAddressId</c> and
/// <c>OrderPayment</c>. The lesson is stable enough to state as a rule: <i>when a conditional
/// uniqueness constraint is needed, make the condition the existence of a row.</i>
/// </para>
/// </summary>
public sealed class PaymentClaim
{
    /// <summary>
    /// The primary key. Not a surrogate id — the natural key <i>is</i> the constraint, and
    /// adding a surrogate would mean the thing being enforced was no longer the key.
    /// </summary>
    public Guid OrderId { get; private init; }

    public Guid PaymentId { get; private init; }

    public DateTimeOffset ClaimedAtUtc { get; private init; }

    private PaymentClaim() { }

    internal static PaymentClaim For(Payment payment, DateTimeOffset nowUtc) =>
        new()
        {
            OrderId = payment.OrderId,
            PaymentId = payment.Id,
            ClaimedAtUtc = nowUtc
        };
}
