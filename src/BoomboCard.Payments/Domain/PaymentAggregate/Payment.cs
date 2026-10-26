using BoomboCard.Payments.Domain.PaymentAggregate.Events;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Payments.Domain.PaymentAggregate;

/// <summary>
/// One attempt to take money for one order, and everything that happened while trying.
///
/// Invariants it holds:
///  - The amount is fixed at creation from the order and never changes.
///  - A settled status is unreachable without a provider reference.
///  - Every provider interaction leaves an attempt row.
///  - Refunds never exceed what was captured, and the refunded total equals the sum of its rows.
///  - A decline is terminal; a failure is not.
///
/// <para>
/// <b>Fourteen Onyx findings converge on this aggregate and the controller that replaced it.</b>
/// The shape of the fix is three decisions, and each one closes a different cluster:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>The intent commits before any provider is contacted.</b> <c>Pending</c> is a real,
/// persisted state with a durable idempotency key, so the HTTP call happens outside every
/// transaction. That is <b>A12</b> — Onyx opened a transaction, made up to three gateway calls
/// inside it with the default 100-second <c>HttpClient</c> timeout, then committed, which on its
/// single-writer SQLite database blocked every write in the system for up to five minutes per
/// request. One slow provider was a total outage.
/// </item>
/// <item>
/// <b>Only a provider response can move the status forward.</b> <see cref="Authorize"/> and
/// <see cref="Capture"/> both require a <see cref="ProviderReference"/>, which can only be
/// constructed from a provider response. There is no path to <c>Captured</c> that does not pass
/// through one. That is <b>M1</b> and <b>M4</b>.
/// </item>
/// <item>
/// <b>Every interaction is a row.</b> Success, decline, timeout and transport failure all append
/// a <see cref="PaymentAttempt"/> carrying the idempotency key, the correlation id, the provider's
/// code and the latency. That is <b>A17</b>, and it is what makes <b>A16</b>'s empty catch blocks
/// impossible to reintroduce without the absence being visible.
/// </item>
/// </list>
/// </summary>
public sealed class Payment : AggregateRoot
{
    /// <summary>
    /// How many times a submission may be attempted before the payment is marked
    /// <c>Failed</c> and left for reconciliation.
    ///
    /// <para>
    /// Three, as Onyx used — the number was never the problem. The problems were that the
    /// attempts carried no idempotency key (<b>M5</b>), that the result was never inspected
    /// (<b>M1</b>), and that a decline counted towards the budget as if it were a network blip.
    /// Here only a transport failure consumes an attempt.
    /// </para>
    /// </summary>
    public const int MaxAttempts = 3;

    private readonly List<PaymentAttempt> _attempts = [];
    private readonly List<Refund> _refunds = [];

    public Guid OrderId { get; private init; }

    public Guid CustomerId { get; private init; }

    /// <summary>
    /// The checkout session the order came from. Carried so a payment failure can release the
    /// holds placed under it — the reference every module keyed its hold on.
    /// </summary>
    public Guid CheckoutSessionId { get; private init; }

    /// <summary>
    /// Our customer-facing reference. Crypto-random, and <b>not</b> the provider's — see
    /// <see cref="PaymentReference"/> for why conflating the two is <b>M4</b>.
    /// </summary>
    public PaymentReference Reference { get; private init; } = null!;

    /// <summary>
    /// Sent on every attempt, generated once, never regenerated. <b>M5.</b>
    /// </summary>
    public IdempotencyKey IdempotencyKey { get; private init; } = null!;

    /// <summary>
    /// <b>Derived server-side from the order, never supplied by a caller.</b> <b>M2</b>: Onyx's
    /// order request carried the points to redeem and its total was computed from a basket the
    /// client described. Here the figure comes from <c>GetOrderSummaryQuery</c> inside the
    /// handler, and this property has no setter for anything else to reach.
    /// </summary>
    public Money Amount { get; private init; } = null!;

    public PaymentStatus Status { get; private set; } = PaymentStatus.Pending;

    /// <summary>
    /// The provider's identifier, or <c>null</c>. Nullable by design and never defaulted —
    /// <b>M4</b>.
    /// </summary>
    public ProviderReference? ProviderReference { get; private set; }

    /// <summary>The provider's decline code, when there is one. Never invented.</summary>
    public string? DeclineCode { get; private set; }

    /// <summary>
    /// How much has been given back. State, with <see cref="Refunds"/> as the log; asserted
    /// equal by <c>payments.integrity-check</c>.
    /// </summary>
    public Money RefundedAmount { get; private set; } = null!;

    /// <summary>
    /// How many submission attempts have been spent. Only transport failures count — see
    /// <see cref="MaxAttempts"/>.
    /// </summary>
    public int SubmissionAttempts { get; private set; }

    public DateTimeOffset InitiatedAtUtc { get; private init; }

    public DateTimeOffset? AuthorizedAtUtc { get; private set; }

    public DateTimeOffset? CapturedAtUtc { get; private set; }

    public DateTimeOffset? SettledOrFailedAtUtc { get; private set; }

    /// <summary>
    /// When the provider was last asked something and did not answer. The reconciliation job's
    /// query predicate — a payment with an indeterminate attempt and no settled status is one
    /// whose real outcome is still unknown.
    /// </summary>
    public DateTimeOffset? IndeterminateSinceUtc { get; private set; }

    public IReadOnlyCollection<PaymentAttempt> Attempts => _attempts.AsReadOnly();

    public IReadOnlyCollection<Refund> Refunds => _refunds.AsReadOnly();

    private Payment() { }

    /// <summary>
    /// Creates the intent. <b>No provider is contacted and nothing is charged.</b>
    ///
    /// <para>
    /// This is TX A of two, and separating it from the provider call is the whole of <b>A12</b>.
    /// It also makes the idempotency key durable before the first attempt, without which a crash
    /// mid-attempt would lose the only thing making a retry safe.
    /// </para>
    /// </summary>
    public static ErrorOr<Payment> Initiate(
        Guid orderId,
        Guid customerId,
        Guid checkoutSessionId,
        Money amount,
        DateTimeOffset nowUtc)
    {
        if (orderId == Guid.Empty)
        {
            return PaymentErrors.OrderRequired;
        }

        if (customerId == Guid.Empty)
        {
            return PaymentErrors.CustomerRequired;
        }

        // A zero-amount payment is not a payment. Refusing it here rather than discovering it at
        // the provider keeps a meaningless request off the wire — and a free order should not
        // reach this module at all.
        if (amount.IsZero)
        {
            return PaymentErrors.AmountMustBePositive;
        }

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customerId,
            CheckoutSessionId = checkoutSessionId,
            Reference = PaymentReference.New(),
            IdempotencyKey = IdempotencyKey.New(),
            Amount = amount,
            RefundedAmount = Money.Zero(amount.CurrencyCode),
            Status = PaymentStatus.Pending,
            InitiatedAtUtc = nowUtc
        };

        payment.RaiseDomainEvent(new PaymentInitiatedEvent(
            payment.Id, orderId, customerId, amount.Amount, amount.CurrencyCode, nowUtc));

        return payment;
    }

    /// <summary>Whether another submission is allowed, by both state and budget.</summary>
    public bool CanSubmit => Status.CanSubmit && SubmissionAttempts < MaxAttempts;

    /// <summary>
    /// The provider reserved the funds.
    ///
    /// <para>
    /// <b><paramref name="providerReference"/> is required, and that requirement is M1 and M4.</b>
    /// There is no overload without it and no default for it. A caller holding no provider
    /// response has nothing to pass, so cannot reach this state — which is the difference between
    /// a rule and a convention.
    /// </para>
    /// </summary>
    public ErrorOr<Success> Authorize(
        ProviderReference providerReference,
        DateTimeOffset nowUtc)
    {
        var status = Status.Authorize();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        ProviderReference = providerReference;
        AuthorizedAtUtc = nowUtc;
        DeclineCode = null;

        // Answered, so no longer unknown.
        IndeterminateSinceUtc = null;

        RaiseDomainEvent(new PaymentAuthorizedEvent(
            Id, OrderId, CustomerId, Amount.Amount, Amount.CurrencyCode,
            providerReference.Value, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// The money moved. <b>The event this raises is what tells Sales the order is paid</b> — and
    /// it is the trigger Phase 4's <c>MarkOrderPaidCommand</c> documented itself as waiting for.
    /// </summary>
    public ErrorOr<Success> Capture(
        ProviderReference providerReference,
        DateTimeOffset nowUtc)
    {
        var status = Status.Capture();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        ProviderReference = providerReference;
        CapturedAtUtc = nowUtc;
        SettledOrFailedAtUtc = nowUtc;
        AuthorizedAtUtc ??= nowUtc;
        DeclineCode = null;
        IndeterminateSinceUtc = null;

        RaiseDomainEvent(new PaymentCapturedEvent(
            Id, OrderId, CustomerId, CheckoutSessionId, Amount.Amount, Amount.CurrencyCode,
            providerReference.Value, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// The provider refused.
    ///
    /// <para>
    /// Terminal, and the decline code is recorded because "declined" on its own is useless to the
    /// customer: <c>insufficient_funds</c> means try another card, <c>expired_card</c> means
    /// update the details, and <c>do_not_honor</c> means call the bank. Onyx had no declined path
    /// at all, so it had nothing to tell anyone.
    /// </para>
    ///
    /// <para>
    /// A decline does <b>not</b> consume a submission attempt, because there is nothing to retry.
    /// </para>
    /// </summary>
    public ErrorOr<Success> Decline(string? declineCode, DateTimeOffset nowUtc)
    {
        var status = Status.Decline();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        DeclineCode = string.IsNullOrWhiteSpace(declineCode) ? null : declineCode.Trim();
        SettledOrFailedAtUtc = nowUtc;
        IndeterminateSinceUtc = null;

        RaiseDomainEvent(new PaymentDeclinedEvent(
            Id, OrderId, CustomerId, CheckoutSessionId, DeclineCode, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// We could not get an answer.
    ///
    /// <para>
    /// <paramref name="outcomeIsUnknown"/> separates the two cases that look identical from the
    /// outside and are completely different in consequence. A transport failure means the
    /// provider never saw the request; an <c>Indeterminate</c> outcome means it may have charged
    /// the card and we lost the reply. The second sets <see cref="IndeterminateSinceUtc"/>, which
    /// is what <c>payments.reconciliation</c> looks for.
    /// </para>
    ///
    /// <para>
    /// A <c>Failed</c> payment is deliberately not terminal. Onyx's bug was treating "no answer"
    /// as an answer; closing the record here would repeat it in the opposite direction.
    /// </para>
    /// </summary>
    public ErrorOr<Success> Fail(bool outcomeIsUnknown, DateTimeOffset nowUtc)
    {
        var status = Status.Fail();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        SubmissionAttempts++;
        SettledOrFailedAtUtc = nowUtc;

        if (outcomeIsUnknown)
        {
            IndeterminateSinceUtc ??= nowUtc;
        }

        RaiseDomainEvent(new PaymentFailedEvent(
            Id, OrderId, CustomerId, CheckoutSessionId, outcomeIsUnknown,
            SubmissionAttempts >= MaxAttempts, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// Gives money back, in whole or in part.
    ///
    /// <para>
    /// <b>Bounded by what was actually captured</b>, summed from the refund rows rather than
    /// trusted from the caller. That is the same lesson as BUGS-AND-LESSONS section 13 — a
    /// release is capped by the receiver, because a caller that knows how much it may give back
    /// is a caller being trusted with somebody else's money.
    /// </para>
    /// </summary>
    public ErrorOr<Refund> RecordRefund(
        Money amount,
        ProviderReference providerReference,
        string reason,
        string idempotencyKey,
        DateTimeOffset nowUtc)
    {
        if (amount.IsZero)
        {
            return PaymentErrors.AmountMustBePositive;
        }

        if (amount.CurrencyCode != Amount.CurrencyCode)
        {
            return PaymentErrors.CurrencyMismatch(Amount.CurrencyCode, amount.CurrencyCode);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return PaymentErrors.RefundKeyRequired;
        }

        // Already applied. A redelivered webhook or a double-clicked button lands here, and the
        // unique index on (PaymentId, IdempotencyKey) is the guarantee behind this read.
        if (_refunds.Any(refund => refund.IdempotencyKey == idempotencyKey))
        {
            return PaymentErrors.RefundAlreadyRecorded;
        }

        var alreadyRefunded = _refunds.Sum(refund => refund.Amount.Amount);
        var remaining = Amount.Amount - alreadyRefunded;

        if (amount.Amount > remaining)
        {
            return PaymentErrors.RefundExceedsCapture(remaining, Amount.CurrencyCode);
        }

        var fully = amount.Amount == remaining;

        var status = Status.Refund(fully);

        if (status.IsError)
        {
            return status.Errors;
        }

        var refund = Refund.Record(
            Id, amount, providerReference.Value, reason, idempotencyKey, nowUtc);

        _refunds.Add(refund);
        Status = status.Value;
        RefundedAmount = Money.Create(alreadyRefunded + amount.Amount, Amount.CurrencyCode).Value;

        RaiseDomainEvent(new PaymentRefundedEvent(
            Id, OrderId, CustomerId, amount.Amount, Amount.CurrencyCode,
            RefundedAmount.Amount, fully, providerReference.Value, reason, nowUtc));

        return refund;
    }

    /// <summary>
    /// The cardholder disputed the charge and the provider took the money back.
    ///
    /// <para>
    /// Recorded with the provider's dispute reference so it can be defended. Onyx <b>M4</b> made
    /// defending one impossible: a fabricated gateway reference leaves nothing to match against
    /// the provider's own record of the transaction.
    /// </para>
    /// </summary>
    public ErrorOr<Success> RecordChargeback(string disputeReference, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(disputeReference))
        {
            return PaymentErrors.DisputeReferenceRequired;
        }

        var status = Status.ChargeBack();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        RaiseDomainEvent(new PaymentChargedBackEvent(
            Id, OrderId, CustomerId, Amount.Amount, Amount.CurrencyCode,
            disputeReference.Trim(), nowUtc));

        return Result.Success;
    }

    /// <summary>Abandoned before any provider was contacted.</summary>
    public ErrorOr<Success> Cancel(string reason, DateTimeOffset nowUtc)
    {
        var status = Status.Cancel();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;
        SettledOrFailedAtUtc = nowUtc;

        RaiseDomainEvent(new PaymentCancelledEvent(
            Id, OrderId, CustomerId, CheckoutSessionId, reason, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// Appends an attempt row. <b>Called for every provider interaction, whatever the outcome.</b>
    ///
    /// <para>
    /// Separate from the status transitions on purpose: an attempt is recorded even when it
    /// changes nothing — an enquiry that found nothing, a decline on an already-declined payment
    /// — and the history is only useful if it is complete. <b>A17.</b>
    /// </para>
    /// </summary>
    public PaymentAttempt RecordAttempt(
        PaymentOperation operation,
        AttemptOutcome outcome,
        string correlationId,
        string? providerReference,
        string? providerCode,
        string? detail,
        int latencyMs,
        DateTimeOffset nowUtc)
    {
        var attemptNumber = _attempts.Count(attempt => attempt.Operation == operation) + 1;

        var record = PaymentAttempt.Record(
            Id,
            attemptNumber,
            operation,
            outcome,
            IdempotencyKey.Value,
            correlationId,
            providerReference,
            providerCode,
            detail,
            latencyMs,
            nowUtc);

        _attempts.Add(record);

        return record;
    }

    /// <summary>
    /// The invariant <c>payments.integrity-check</c> asserts: the refunded total equals the sum
    /// of the refund rows, and never exceeds the amount captured.
    ///
    /// <para>
    /// The fifth state-versus-log pairing in this codebase, after stock, credentials, promotion
    /// redemptions and loyalty points. Worth noting what BUGS-AND-LESSONS section 13 taught about
    /// this check: agreement between a counter and its log proves they are <i>consistent</i>, not
    /// that either is <i>right</i>. So the second clause is the one with teeth — a refunded total
    /// above the captured amount is money leaving that never arrived.
    /// </para>
    /// </summary>
    public bool RefundsAreConsistent() =>
        _refunds.Sum(refund => refund.Amount.Amount) == RefundedAmount.Amount
            && RefundedAmount.Amount <= Amount.Amount;
}

public static class PaymentErrors
{
    public static Error NotFound(Guid paymentId) =>
        Error.NotFound("Payment.NotFound", $"No payment with id '{paymentId}'.");

    public static Error NotFoundByReference(string reference) =>
        Error.NotFound("Payment.NotFound", $"No payment with reference '{reference}'.");

    public static Error OrderRequired =>
        Error.Validation("Payment.OrderRequired", "A payment must name an order.");

    public static Error CustomerRequired =>
        Error.Validation("Payment.CustomerRequired", "A payment must name a customer.");

    public static Error AmountMustBePositive =>
        Error.Validation("Payment.AmountMustBePositive", "A payment amount must be more than zero.");

    public static Error AttemptsExhausted =>
        Error.Conflict(
            "Payment.AttemptsExhausted",
            $"This payment has used all {Payment.MaxAttempts} submission attempts. Its real "
            + "outcome is resolved by reconciliation with the provider, not by trying again.");

    public static Error RefundKeyRequired =>
        Error.Validation(
            "Payment.RefundKeyRequired",
            "A refund needs its own idempotency key, so a redelivered instruction refunds once.");

    public static Error RefundAlreadyRecorded =>
        Error.Conflict("Payment.RefundAlreadyRecorded", "This refund has already been recorded.");

    public static Error DisputeReferenceRequired =>
        Error.Validation(
            "Payment.DisputeReferenceRequired",
            "A chargeback must carry the provider's dispute reference, or it cannot be defended.");

    public static Error RefundExceedsCapture(decimal remaining, string currencyCode) =>
        Error.Validation(
            "Payment.RefundExceedsCapture",
            $"Only {remaining:0.00} {currencyCode} of this payment has not already been refunded.");

    public static Error CurrencyMismatch(string expected, string actual) =>
        Error.Validation(
            "Payment.CurrencyMismatch",
            $"This payment is in {expected}; a refund in {actual} cannot be applied to it.");

    public static Error AlreadyInitiated(Guid orderId) =>
        Error.Conflict(
            "Payment.AlreadyInitiated",
            $"Order '{orderId}' already has a live payment. Submit that one rather than starting "
            + "another.");
}
