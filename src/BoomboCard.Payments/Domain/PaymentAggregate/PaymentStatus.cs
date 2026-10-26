using ErrorOr;
using BoomboCard.SharedKernel;

namespace BoomboCard.Payments.Domain.PaymentAggregate;

/// <summary>
/// Where a payment is. <b>Onyx A39</b>: its <c>Payment.Status</c> was
/// <c>public string Status { get; set; } = "Pending"</c>, only ever written as the literal
/// <c>"Paid"</c>, with no representation for declined or refunded at all — while
/// <c>OrderStatus</c> next to it was a proper enum.
/// </summary>
public enum PaymentStatusValue
{
    /// <summary>
    /// The intent exists and the amount is fixed. No provider has been contacted. Committed
    /// before any HTTP call, which is what makes <b>A12</b> avoidable.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// The provider has reserved the funds and told us so. A provider reference is present.
    /// </summary>
    Authorized = 2,

    /// <summary>The money has moved. A provider reference is present.</summary>
    Captured = 3,

    /// <summary>
    /// The provider refused. <b>Definitive</b> — a decline is an answer, not a failure, and it
    /// is never retried.
    /// </summary>
    Declined = 4,

    /// <summary>
    /// We could not get an answer: transport failure, timeout, or a malformed response, after
    /// the retry budget was exhausted.
    ///
    /// <para>
    /// <b>This is not the same as declined, and conflating them is Onyx M1.</b> A failed payment
    /// may have succeeded at the provider — a lost response to a successful charge looks exactly
    /// like a timeout — which is why <c>payments.reconciliation</c> asks the provider by
    /// idempotency key rather than assuming.
    /// </para>
    /// </summary>
    Failed = 5,

    /// <summary>Some of a captured payment has been given back.</summary>
    PartiallyRefunded = 6,

    /// <summary>All of a captured payment has been given back.</summary>
    Refunded = 7,

    /// <summary>
    /// The cardholder disputed the charge and the provider pulled the funds. Terminal, and
    /// recorded with the provider's dispute reference so it can be defended — Onyx <b>M4</b>
    /// made this impossible, because a fabricated gateway reference leaves nothing to defend
    /// with.
    /// </summary>
    ChargedBack = 8,

    /// <summary>Abandoned before any provider was contacted.</summary>
    Cancelled = 9
}

/// <summary>
/// The payment state machine. Every transition is a guarded method that returns the next status
/// or an error naming why not.
///
/// <para>
/// <b>Onyx M1 is the finding this type exists for</b>, and it is worth quoting because the shape
/// matters more than the words:
/// </para>
///
/// <code>
/// for (var attempt = 0; attempt &lt; 3; attempt++)
/// {
///     try { result = _gateway.ChargeAsync(...).Result; if (result.Success) break; }
///     catch { }
/// }
/// // result may be null, or Success == false — neither is checked
/// var payment = new Payment { ..., Status = "Paid", ... };
/// order.Status = OrderStatus.Paid;
/// </code>
///
/// <para>
/// A declined card, a provider outage, a DNS failure and three timeouts all produced a payment
/// row reading <c>"Paid"</c>, an order marked paid, and a success response with a confirmation
/// code. The register's note is the part that stops you sleeping: <i>the configured gateway host
/// does not resolve, so every payment ever taken by this system is in this state.</i>
/// </para>
///
/// <para>
/// There was no declined path <b>anywhere in the system</b>. So the rule here is stated as an
/// invariant rather than a convention: <b>the only way to reach a settled status is to pass a
/// provider reference, and the only source of a provider reference is a provider response.</b>
/// Absence of a positive answer is never success — see <see cref="Authorize"/>.
/// </para>
/// </summary>
public sealed class PaymentStatus : ValueObject
{
    public PaymentStatusValue Value { get; }

    private PaymentStatus(PaymentStatusValue value) => Value = value;

    public static PaymentStatus Pending => new(PaymentStatusValue.Pending);

    public static PaymentStatus From(PaymentStatusValue value) => new(value);

    public bool IsPending => Value is PaymentStatusValue.Pending;

    public bool IsAuthorized => Value is PaymentStatusValue.Authorized;

    public bool IsCaptured => Value is PaymentStatusValue.Captured;

    public bool IsDeclined => Value is PaymentStatusValue.Declined;

    public bool IsFailed => Value is PaymentStatusValue.Failed;

    /// <summary>
    /// Whether the provider has given a positive answer, so money is either reserved or taken.
    /// These are the statuses that require a provider reference.
    /// </summary>
    public bool IsSettled =>
        Value is PaymentStatusValue.Authorized
            or PaymentStatusValue.Captured
            or PaymentStatusValue.PartiallyRefunded
            or PaymentStatusValue.Refunded
            or PaymentStatusValue.ChargedBack;

    /// <summary>
    /// Whether this payment is finished with, one way or another. A terminal payment is never
    /// submitted again — which is the state-machine half of <b>M3</b>.
    /// </summary>
    public bool IsTerminal =>
        Value is PaymentStatusValue.Declined
            or PaymentStatusValue.Refunded
            or PaymentStatusValue.ChargedBack
            or PaymentStatusValue.Cancelled;

    /// <summary>
    /// Whether another submission attempt is allowed.
    ///
    /// <para>
    /// <c>Failed</c> is retryable and <c>Declined</c> is not, and that asymmetry is <b>M5</b>:
    /// a transport failure says nothing about the card, while a decline is the provider's
    /// considered answer. Retrying a decline is how a customer's bank starts treating you as an
    /// attacker.
    /// </para>
    /// </summary>
    public bool CanSubmit => Value is PaymentStatusValue.Pending or PaymentStatusValue.Failed;

    /// <summary>
    /// The provider reserved the funds.
    ///
    /// <para>
    /// Reachable only from <c>Pending</c> or <c>Failed</c>. A payment that already authorized
    /// cannot authorize again — that is <b>M3</b>'s "the same order can be paid repeatedly"
    /// closed in the aggregate, with a unique index on the order behind it.
    /// </para>
    /// </summary>
    public ErrorOr<PaymentStatus> Authorize() =>
        Value switch
        {
            PaymentStatusValue.Pending or PaymentStatusValue.Failed =>
                new PaymentStatus(PaymentStatusValue.Authorized),
            PaymentStatusValue.Authorized => PaymentStatusErrors.AlreadyAuthorized,
            PaymentStatusValue.Captured => PaymentStatusErrors.AlreadyCaptured,
            PaymentStatusValue.Declined => PaymentStatusErrors.AlreadyDeclined,
            _ => PaymentStatusErrors.NotSubmittable
        };

    /// <summary>
    /// The money moved.
    ///
    /// <para>
    /// Allowed from <c>Authorized</c> (the two-step flow) and directly from <c>Pending</c> or
    /// <c>Failed</c> (a provider that authorizes and captures in one call). Both are real
    /// provider shapes, so both are modelled rather than one being assumed.
    /// </para>
    /// </summary>
    public ErrorOr<PaymentStatus> Capture() =>
        Value switch
        {
            PaymentStatusValue.Authorized or PaymentStatusValue.Pending or PaymentStatusValue.Failed =>
                new PaymentStatus(PaymentStatusValue.Captured),
            PaymentStatusValue.Captured => PaymentStatusErrors.AlreadyCaptured,
            PaymentStatusValue.Declined => PaymentStatusErrors.AlreadyDeclined,
            _ => PaymentStatusErrors.NotCapturable
        };

    /// <summary>
    /// The provider refused. Definitive and terminal.
    ///
    /// <para>
    /// A decline after a successful capture is not a thing a provider says, so it is refused
    /// rather than accepted — a webhook claiming it would be either a bug or an attack.
    /// </para>
    /// </summary>
    public ErrorOr<PaymentStatus> Decline() =>
        Value switch
        {
            PaymentStatusValue.Pending or PaymentStatusValue.Failed =>
                new PaymentStatus(PaymentStatusValue.Declined),
            PaymentStatusValue.Declined => PaymentStatusErrors.AlreadyDeclined,
            _ => PaymentStatusErrors.NotDeclinable
        };

    /// <summary>
    /// We did not get an answer. <b>Not</b> terminal, because an answer may still arrive — from
    /// a webhook, or from the reconciliation job asking the provider by idempotency key.
    /// </summary>
    public ErrorOr<PaymentStatus> Fail() =>
        Value switch
        {
            PaymentStatusValue.Pending or PaymentStatusValue.Failed =>
                new PaymentStatus(PaymentStatusValue.Failed),
            _ => PaymentStatusErrors.NotFailable
        };

    /// <summary>
    /// Refunded, in whole or in part. The caller decides which by passing whether anything is
    /// left — the aggregate owns that arithmetic, not this type.
    /// </summary>
    public ErrorOr<PaymentStatus> Refund(bool fully) =>
        Value switch
        {
            PaymentStatusValue.Captured or PaymentStatusValue.PartiallyRefunded =>
                new PaymentStatus(fully
                    ? PaymentStatusValue.Refunded
                    : PaymentStatusValue.PartiallyRefunded),
            PaymentStatusValue.Refunded => PaymentStatusErrors.AlreadyRefunded,
            PaymentStatusValue.Authorized => PaymentStatusErrors.NotCaptured,
            _ => PaymentStatusErrors.NotRefundable
        };

    /// <summary>
    /// The cardholder disputed it and the provider took the money back. Reachable from any
    /// settled status, including a refunded one — a dispute can land after a partial refund,
    /// and refusing to record it would mean the data disagreed with the bank statement.
    /// </summary>
    public ErrorOr<PaymentStatus> ChargeBack() =>
        IsSettled && Value is not PaymentStatusValue.ChargedBack
            ? new PaymentStatus(PaymentStatusValue.ChargedBack)
            : Value is PaymentStatusValue.ChargedBack
                ? PaymentStatusErrors.AlreadyChargedBack
                : PaymentStatusErrors.NotChargeBackable;

    /// <summary>
    /// Abandoned before any provider was contacted. <c>Pending</c> only: once a provider has
    /// been asked, the answer decides, and cancelling would be us overruling it.
    /// </summary>
    public ErrorOr<PaymentStatus> Cancel() =>
        Value is PaymentStatusValue.Pending
            ? new PaymentStatus(PaymentStatusValue.Cancelled)
            : PaymentStatusErrors.NotCancellable;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString();
}

public static class PaymentStatusErrors
{
    public static Error AlreadyAuthorized =>
        Error.Conflict("Payment.AlreadyAuthorized", "This payment is already authorized.");

    public static Error AlreadyCaptured =>
        Error.Conflict("Payment.AlreadyCaptured", "This payment has already been captured.");

    public static Error AlreadyDeclined =>
        Error.Conflict(
            "Payment.AlreadyDeclined",
            "This payment was declined. A decline is the provider's answer and is not retried; "
            + "start a new payment instead.");

    public static Error AlreadyRefunded =>
        Error.Conflict("Payment.AlreadyRefunded", "This payment has been refunded in full.");

    public static Error AlreadyChargedBack =>
        Error.Conflict("Payment.AlreadyChargedBack", "This payment has already been charged back.");

    public static Error NotSubmittable =>
        Error.Conflict(
            "Payment.NotSubmittable",
            "This payment is not in a state that can be submitted to a provider.");

    public static Error NotCapturable =>
        Error.Conflict("Payment.NotCapturable", "This payment cannot be captured from its current state.");

    public static Error NotDeclinable =>
        Error.Conflict("Payment.NotDeclinable", "A provider cannot decline a payment in this state.");

    public static Error NotFailable =>
        Error.Conflict("Payment.NotFailable", "This payment has already been answered.");

    public static Error NotCaptured =>
        Error.Conflict(
            "Payment.NotCaptured",
            "An authorized payment has taken no money, so there is nothing to refund. Cancel the "
            + "authorization instead.");

    public static Error NotRefundable =>
        Error.Conflict("Payment.NotRefundable", "This payment cannot be refunded from its current state.");

    public static Error NotChargeBackable =>
        Error.Conflict(
            "Payment.NotChargeBackable",
            "A payment that never settled cannot be charged back.");

    public static Error NotCancellable =>
        Error.Conflict(
            "Payment.NotCancellable",
            "A provider has already been contacted for this payment, so its answer decides the "
            + "outcome.");
}
