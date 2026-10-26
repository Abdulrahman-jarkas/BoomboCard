using BoomboCard.SharedKernel;

namespace BoomboCard.Payments.Domain.PaymentAggregate;

/// <summary>What one provider interaction was trying to do.</summary>
public enum PaymentOperation
{
    Authorize = 1,
    Capture = 2,
    Refund = 3,

    /// <summary>
    /// A read, not a write: "what happened to the request carrying this idempotency key?" The
    /// reconciliation job's question, and the only safe way to resolve a timeout.
    /// </summary>
    Enquire = 4
}

/// <summary>
/// What the provider said, reduced to the three answers that matter.
///
/// <para>
/// <b>The three-way split is the design.</b> Onyx had a two-way <c>bool Success</c>, which forced
/// every timeout into the same bucket as every decline — and that single conflation is <b>M1</b>
/// and <b>M5</b> together. A `bool` cannot express "we do not know", so the code had to guess,
/// and it guessed "success" (M1) while the retry guessed "failure" (M5).
/// </para>
/// </summary>
public enum AttemptOutcome
{
    /// <summary>The provider gave a positive answer and a reference.</summary>
    Succeeded = 1,

    /// <summary>
    /// The provider refused, and said so. <b>Definitive.</b> Never retried: a decline is an
    /// answer.
    /// </summary>
    Declined = 2,

    /// <summary>
    /// The request failed before the provider could answer — connection refused, DNS, 5xx, a
    /// malformed body. Safe to retry under the same idempotency key.
    /// </summary>
    TransportFailure = 3,

    /// <summary>
    /// <b>The one that matters.</b> We sent the request and never learned the outcome: a timeout,
    /// or a connection dropped mid-response. The charge may well have succeeded.
    ///
    /// <para>
    /// This is <i>not</i> a failure and must never be reported to a customer as one. It is
    /// resolved by asking the provider what happened to that idempotency key —
    /// <c>payments.reconciliation</c> — rather than by assuming either way. Onyx treated it as a
    /// failure and charged again.
    /// </para>
    /// </summary>
    Indeterminate = 4
}

/// <summary>
/// An immutable record of one interaction with a payment provider.
///
/// <para>
/// <b>Onyx A17 in full:</b> "Nothing records that three attempts were made, what each returned,
/// or why they failed. When a customer disputes a charge there is no history to consult." The
/// only trace of a payment failure in that system was a <c>Console.WriteLine</c> inside an
/// otherwise empty catch block (<b>A16</b>), which no logging pipeline captured.
/// </para>
///
/// <para>
/// So every attempt is a row, written whether it succeeded or not, carrying what the obligation
/// line asks for: request id, idempotency key, attempt number, provider response, latency,
/// outcome. It is the payment equivalent of <c>StockMovement</c> and <c>PointsLedgerEntry</c> —
/// the log beside the state — and it is what makes a support conversation about a disputed charge
/// possible at all.
/// </para>
///
/// <para>
/// <b>Nothing here is ever updated.</b> A second attempt is a second row, which is what lets the
/// history answer "how many times did we ask, and what changed between asks?".
/// </para>
/// </summary>
public sealed class PaymentAttempt : Entity
{
    public Guid PaymentId { get; private init; }

    /// <summary>1 for the first attempt. Unique per payment and operation at the database.</summary>
    public int AttemptNumber { get; private init; }

    public PaymentOperation Operation { get; private init; }

    public AttemptOutcome Outcome { get; private init; }

    /// <summary>
    /// The key sent with this request. Stored on the attempt as well as the payment, so the row
    /// is self-contained evidence of what was sent — and so a provider enquiry can be matched to
    /// the attempt that prompted it.
    /// </summary>
    public string IdempotencyKey { get; private init; } = string.Empty;

    /// <summary>
    /// Our correlation id for the HTTP request, so an attempt row joins to the log line.
    /// <b>A16</b>: without a correlation id, a logged gateway failure cannot be tied to the
    /// payment it belongs to.
    /// </summary>
    public string CorrelationId { get; private init; } = string.Empty;

    /// <summary>The provider's reference, when the provider gave one. Null otherwise — never faked.</summary>
    public string? ProviderReference { get; private init; }

    /// <summary>The provider's own code, e.g. <c>insufficient_funds</c>. Null when there was no answer.</summary>
    public string? ProviderCode { get; private init; }

    /// <summary>
    /// The provider's message, or our exception type and message for a transport failure.
    ///
    /// <para>
    /// Truncated rather than unbounded: an exception's <c>ToString()</c> can carry a stack trace
    /// of several kilobytes, and a payment under retry would write several of them. The outbox
    /// payload column taught this lesson the expensive way — see BUGS-AND-LESSONS section 9.
    /// </para>
    /// </summary>
    public string? Detail { get; private init; }

    /// <summary>How long the provider took. The figure a latency alert is built on.</summary>
    public int LatencyMs { get; private init; }

    public DateTimeOffset OccurredAtUtc { get; private init; }

    private PaymentAttempt() { }

    internal static PaymentAttempt Record(
        Guid paymentId,
        int attemptNumber,
        PaymentOperation operation,
        AttemptOutcome outcome,
        string idempotencyKey,
        string correlationId,
        string? providerReference,
        string? providerCode,
        string? detail,
        int latencyMs,
        DateTimeOffset occurredAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            AttemptNumber = attemptNumber,
            Operation = operation,
            Outcome = outcome,
            IdempotencyKey = idempotencyKey,
            CorrelationId = correlationId,
            ProviderReference = providerReference,
            ProviderCode = providerCode,
            Detail = Truncate(detail),
            LatencyMs = latencyMs,
            OccurredAtUtc = occurredAtUtc
        };

    public const int MaxDetailLength = 2000;

    private static string? Truncate(string? detail) =>
        detail is null || detail.Length <= MaxDetailLength
            ? detail
            : detail[..MaxDetailLength];
}

/// <summary>
/// Money given back, in whole or in part.
///
/// <para>
/// Its own row rather than a pair of columns on the payment, because partial refunds compose: a
/// £100 capture can be refunded £30 then £20, and a single <c>RefundedAmount</c> column would
/// record the total while losing both reasons and both provider references. The same
/// state-versus-log pairing as everywhere else — <c>Payment.RefundedAmount</c> is the state,
/// these are the log, and <c>payments.integrity-check</c> asserts they agree.
/// </para>
///
/// <para>
/// Every refund carries its own provider reference, because a refund is a separate transaction at
/// the provider and reconciliation has to match it separately. <b>M4</b> again: without it, a
/// refund that failed is indistinguishable from one that went through.
/// </para>
/// </summary>
public sealed class Refund : Entity
{
    public Guid PaymentId { get; private init; }

    public Money Amount { get; private init; } = null!;

    /// <summary>The provider's reference for the refund itself, not for the original charge.</summary>
    public string ProviderReference { get; private init; } = string.Empty;

    /// <summary>Why. Recorded so a statement can explain it rather than just showing a credit.</summary>
    public string Reason { get; private init; } = string.Empty;

    /// <summary>
    /// The caller's own idempotency key for this refund, unique at the database — so a
    /// redelivered refund webhook or a double-clicked admin button refunds once.
    /// </summary>
    public string IdempotencyKey { get; private init; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; private init; }

    private Refund() { }

    internal static Refund Record(
        Guid paymentId,
        Money amount,
        string providerReference,
        string reason,
        string idempotencyKey,
        DateTimeOffset occurredAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            Amount = amount,
            ProviderReference = providerReference,
            Reason = reason,
            IdempotencyKey = idempotencyKey,
            OccurredAtUtc = occurredAtUtc
        };
}
