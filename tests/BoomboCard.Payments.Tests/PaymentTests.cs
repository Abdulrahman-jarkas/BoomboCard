using BoomboCard.Payments.Domain.PaymentAggregate;
using BoomboCard.Payments.Domain.PaymentAggregate.Events;
using BoomboCard.SharedKernel;

namespace BoomboCard.Payments.Tests;

/// <summary>
/// The aggregate. <b>These are the tests that keep M1, M4, M5, A17 and A39 closed.</b>
///
/// <para>
/// Fourteen Onyx findings converge on the payment path, and the four Critical ones are all
/// reachable from a single sentence in its register: <i>"the configured gateway host does not
/// resolve, so every payment ever taken by this system is in this state"</i> — where "this state"
/// is a row reading <c>"Paid"</c> for money that never moved.
/// </para>
/// </summary>
public class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

    private static Money Usd(decimal amount) => Money.Create(amount, "USD").Value;

    private static ProviderReference Reference(string value = "ch_provider_1") =>
        ProviderReference.FromProvider(value).Value;

    private static Payment NewPayment(decimal amount = 100.00m)
    {
        var payment = Payment.Initiate(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Usd(amount), Now);

        Assert.False(payment.IsError, payment.IsError ? payment.FirstError.Description : "");

        return payment.Value;
    }

    private static Payment CapturedPayment(decimal amount = 100.00m)
    {
        var payment = NewPayment(amount);
        Assert.False(payment.Capture(Reference(), Now).IsError);

        return payment;
    }

    // ------------------------------------------------------------------ the intent

    /// <summary>
    /// A new payment has contacted nobody, has no provider reference, and already has a durable
    /// idempotency key.
    ///
    /// <para>
    /// The key existing <i>before</i> the first attempt is the part that matters. A key generated
    /// in memory at attempt time and lost to a crash is not an idempotency key, and the retry
    /// that follows is <b>M5</b> again.
    /// </para>
    /// </summary>
    [Fact]
    public void ANewPaymentIsPendingWithAKeyAndNoProviderReference()
    {
        var payment = NewPayment();

        Assert.True(payment.Status.IsPending);
        Assert.Null(payment.ProviderReference);
        Assert.Null(payment.DeclineCode);
        Assert.Equal(0, payment.SubmissionAttempts);
        Assert.Empty(payment.Attempts);
        Assert.Empty(payment.Refunds);
        Assert.Equal(IdempotencyKey.Length, payment.IdempotencyKey.Value.Length);
        Assert.StartsWith("PAY-", payment.Reference.Value);
        Assert.Equal(0m, payment.RefundedAmount.Amount);
        Assert.True(payment.CanSubmit);
    }

    /// <summary>
    /// <b>The idempotency key never changes.</b> Not across attempts, not across failures — a key
    /// that changes between attempts is a correlation id, not an idempotency key, and provides no
    /// basis for a provider to deduplicate.
    /// </summary>
    [Fact]
    public void TheIdempotencyKeyIsStableAcrossEveryAttempt()
    {
        var payment = NewPayment();
        var key = payment.IdempotencyKey.Value;

        Assert.False(payment.Fail(outcomeIsUnknown: false, Now).IsError);
        Assert.Equal(key, payment.IdempotencyKey.Value);

        Assert.False(payment.Fail(outcomeIsUnknown: true, Now).IsError);
        Assert.Equal(key, payment.IdempotencyKey.Value);

        Assert.False(payment.Capture(Reference(), Now).IsError);
        Assert.Equal(key, payment.IdempotencyKey.Value);

        // And every attempt row carries it, so the row is self-contained evidence of what was
        // sent — which is what lets a provider enquiry be matched to the attempt that prompted it.
        payment.RecordAttempt(
            PaymentOperation.Authorize, AttemptOutcome.Succeeded, "corr", "ch_1", null, null, 10, Now);

        Assert.All(payment.Attempts, attempt => Assert.Equal(key, attempt.IdempotencyKey));
    }

    /// <summary>Two payments never share a key, or a provider would deduplicate one against the other.</summary>
    [Fact]
    public void TwoPaymentsNeverShareAnIdempotencyKeyOrAReference()
    {
        var keys = new HashSet<string>();
        var references = new HashSet<string>();

        for (var index = 0; index < 500; index++)
        {
            var payment = NewPayment();

            Assert.True(keys.Add(payment.IdempotencyKey.Value));
            Assert.True(references.Add(payment.Reference.Value));
        }
    }

    [Fact]
    public void APaymentMustNameAnOrderACustomerAndAPositiveAmount()
    {
        Assert.True(
            Payment.Initiate(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Usd(10m), Now).IsError);

        Assert.True(
            Payment.Initiate(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), Usd(10m), Now).IsError);

        // A zero-amount payment is not a payment, and refusing it here keeps a meaningless
        // request off the wire.
        Assert.Equal(
            "Payment.AmountMustBePositive",
            Payment.Initiate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Usd(0m), Now)
                .FirstError.Code);
    }

    // ------------------------------------------------------------------ M1 and M4

    /// <summary>
    /// <b>M4, and the reason it is a type rather than a rule.</b> A settled status requires a
    /// <see cref="ProviderReference"/>, and that type can only be constructed from a non-empty
    /// provider response.
    ///
    /// <para>
    /// Onyx <b>M4</b> is one line —
    /// <c>GatewayReference = result?.GatewayReference ?? confirmationCode</c> — which stored a
    /// local random six-digit code in the field whose purpose is to identify the transaction at
    /// the provider. Here there is no overload of <c>Capture</c> that takes a string, so a caller
    /// holding no answer has nothing to pass.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AProviderReferenceCannotBeBuiltFromAMissingAnswer(string? value)
    {
        var reference = ProviderReference.FromProvider(value);

        Assert.True(reference.IsError);
        Assert.Equal("ProviderReference.Missing", reference.FirstError.Code);
    }

    [Fact]
    public void CapturingSetsTheProviderReferenceAndClearsAnyDeclineCode()
    {
        var payment = NewPayment();

        Assert.False(payment.Fail(outcomeIsUnknown: false, Now).IsError);
        Assert.False(payment.Capture(Reference("ch_real_123"), Now.AddSeconds(5)).IsError);

        Assert.True(payment.Status.IsCaptured);
        Assert.Equal("ch_real_123", payment.ProviderReference!.Value);
        Assert.Null(payment.DeclineCode);
        Assert.Equal(Now.AddSeconds(5), payment.CapturedAtUtc);
        Assert.Equal(Now.AddSeconds(5), payment.AuthorizedAtUtc);
    }

    /// <summary>
    /// <b>No settled payment lacks a provider reference, by construction.</b> This asserts the
    /// invariant across the whole settled set rather than at one transition — the same shape
    /// <c>payments.integrity-check</c> asserts continuously in production.
    /// </summary>
    [Fact]
    public void NoPathReachesASettledStatusWithoutAProviderReference()
    {
        var captured = CapturedPayment();
        Assert.True(captured.Status.IsSettled);
        Assert.NotNull(captured.ProviderReference);

        var authorized = NewPayment();
        Assert.False(authorized.Authorize(Reference(), Now).IsError);
        Assert.True(authorized.Status.IsSettled);
        Assert.NotNull(authorized.ProviderReference);

        var refunded = CapturedPayment();
        Assert.False(refunded.RecordRefund(Usd(100m), Reference("re_1"), "goodwill", "k1", Now).IsError);
        Assert.True(refunded.Status.IsSettled);
        Assert.NotNull(refunded.ProviderReference);

        // And the statuses that are NOT settled all have a null reference.
        var declined = NewPayment();
        Assert.False(declined.Decline("insufficient_funds", Now).IsError);
        Assert.Null(declined.ProviderReference);

        var failed = NewPayment();
        Assert.False(failed.Fail(outcomeIsUnknown: true, Now).IsError);
        Assert.Null(failed.ProviderReference);
    }

    /// <summary>
    /// A decline records the provider's code, because "declined" alone is useless to the
    /// customer: <c>insufficient_funds</c> means try another card, <c>expired_card</c> means
    /// update the details, <c>do_not_honor</c> means call the bank.
    ///
    /// <para>
    /// Onyx had no declined path anywhere in the system, so it had nothing to tell anyone.
    /// </para>
    /// </summary>
    [Fact]
    public void ADeclineRecordsTheProvidersCodeAndTakesNoProviderReference()
    {
        var payment = NewPayment();

        Assert.False(payment.Decline("insufficient_funds", Now).IsError);

        Assert.True(payment.Status.IsDeclined);
        Assert.Equal("insufficient_funds", payment.DeclineCode);
        Assert.Null(payment.ProviderReference);
        Assert.False(payment.CanSubmit);
    }

    /// <summary>
    /// A decline does not consume a submission attempt, because there is nothing to retry. Onyx
    /// counted everything against one budget of three.
    /// </summary>
    [Fact]
    public void ADeclineDoesNotConsumeASubmissionAttempt()
    {
        var payment = NewPayment();

        Assert.False(payment.Decline("do_not_honor", Now).IsError);

        Assert.Equal(0, payment.SubmissionAttempts);
    }

    // ------------------------------------------------------------------ M5: the unknown outcome

    /// <summary>
    /// <b>The distinction the whole module turns on.</b> A transport failure means the provider
    /// never saw the request, so the outcome is known: nothing happened. An indeterminate outcome
    /// means we sent it and lost the reply, so the card may well have been charged.
    ///
    /// <para>
    /// <c>IndeterminateSinceUtc</c> is what <c>payments.reconciliation</c> looks for, and what
    /// stops a consumer releasing the order's holds for a charge that may have succeeded.
    /// </para>
    /// </summary>
    [Fact]
    public void ATransportFailureIsKnownAndAnIndeterminateOutcomeIsNot()
    {
        var known = NewPayment();
        Assert.False(known.Fail(outcomeIsUnknown: false, Now).IsError);

        Assert.True(known.Status.IsFailed);
        Assert.Null(known.IndeterminateSinceUtc);
        Assert.Equal(1, known.SubmissionAttempts);

        var unknown = NewPayment();
        Assert.False(unknown.Fail(outcomeIsUnknown: true, Now).IsError);

        Assert.True(unknown.Status.IsFailed);
        Assert.Equal(Now, unknown.IndeterminateSinceUtc);
    }

    /// <summary>
    /// The first moment the outcome became unknown is kept, not the latest. The reconciliation
    /// job's grace period is measured from when we <i>started</i> not knowing — a later attempt
    /// must not reset the clock on a charge that has been in doubt for an hour.
    /// </summary>
    [Fact]
    public void TheFirstMomentOfDoubtIsTheOneThatIsKept()
    {
        var payment = NewPayment();

        Assert.False(payment.Fail(outcomeIsUnknown: true, Now).IsError);
        Assert.False(payment.Fail(outcomeIsUnknown: true, Now.AddMinutes(10)).IsError);

        Assert.Equal(Now, payment.IndeterminateSinceUtc);
    }

    /// <summary>An answer, from wherever it comes, clears the doubt.</summary>
    [Fact]
    public void AnAnswerClearsTheDoubt()
    {
        var payment = NewPayment();
        Assert.False(payment.Fail(outcomeIsUnknown: true, Now).IsError);
        Assert.NotNull(payment.IndeterminateSinceUtc);

        // This is the reconciliation job's path: the provider was asked and said the charge
        // succeeded all along.
        Assert.False(payment.Capture(Reference(), Now.AddMinutes(2)).IsError);

        Assert.Null(payment.IndeterminateSinceUtc);
        Assert.True(payment.Status.IsCaptured);
    }

    /// <summary>
    /// The attempt budget bounds how many times a card is presented, and <c>CanSubmit</c>
    /// combines it with the status — so an exhausted payment is refused by state, not by a
    /// caller remembering to count.
    /// </summary>
    [Fact]
    public void TheAttemptBudgetIsExhaustedAfterThreeFailures()
    {
        var payment = NewPayment();

        for (var attempt = 1; attempt <= Payment.MaxAttempts; attempt++)
        {
            Assert.True(payment.CanSubmit, $"should be submittable before attempt {attempt}");
            Assert.False(payment.Fail(outcomeIsUnknown: false, Now).IsError);
        }

        Assert.Equal(Payment.MaxAttempts, payment.SubmissionAttempts);
        Assert.False(payment.CanSubmit);

        // The STATUS still allows it — Failed is retryable — and the budget is what refuses.
        // Both halves matter, which is why CanSubmit is on the aggregate and not on the status.
        Assert.True(payment.Status.CanSubmit);
    }

    // ------------------------------------------------------------------ A17: the attempt log

    /// <summary>
    /// <b>A17.</b> "Nothing records that three attempts were made, what each returned, or why
    /// they failed. When a customer disputes a charge there is no history to consult."
    ///
    /// <para>
    /// Every interaction is a row, numbered per operation, carrying the key, the correlation id,
    /// the provider's code and the latency. Nothing is ever updated — a second attempt is a
    /// second row, which is what lets the history answer "what changed between asks?".
    /// </para>
    /// </summary>
    [Fact]
    public void EveryInteractionLeavesANumberedRow()
    {
        var payment = NewPayment();

        payment.RecordAttempt(
            PaymentOperation.Authorize, AttemptOutcome.TransportFailure,
            "corr-1", null, null, "connection refused", 15, Now);

        payment.RecordAttempt(
            PaymentOperation.Authorize, AttemptOutcome.Indeterminate,
            "corr-2", null, null, "timeout", 30_000, Now.AddSeconds(30));

        payment.RecordAttempt(
            PaymentOperation.Authorize, AttemptOutcome.Succeeded,
            "corr-3", "ch_1", null, null, 120, Now.AddMinutes(1));

        Assert.Equal(3, payment.Attempts.Count);
        Assert.Equal([1, 2, 3], payment.Attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal(
            [AttemptOutcome.TransportFailure, AttemptOutcome.Indeterminate, AttemptOutcome.Succeeded],
            payment.Attempts.Select(attempt => attempt.Outcome));

        // Latency is recorded, which is what a latency alert and a "why was that slow?"
        // conversation are both built on.
        Assert.Equal(30_000, payment.Attempts.ElementAt(1).LatencyMs);
    }

    /// <summary>Numbering is per operation, so a refund is attempt 1 of its own kind.</summary>
    [Fact]
    public void AttemptsAreNumberedPerOperation()
    {
        var payment = NewPayment();

        payment.RecordAttempt(
            PaymentOperation.Authorize, AttemptOutcome.Succeeded, "c1", "ch_1", null, null, 10, Now);
        payment.RecordAttempt(
            PaymentOperation.Refund, AttemptOutcome.Succeeded, "c2", "re_1", null, null, 10, Now);
        payment.RecordAttempt(
            PaymentOperation.Refund, AttemptOutcome.Succeeded, "c3", "re_2", null, null, 10, Now);

        var refunds = payment.Attempts
            .Where(attempt => attempt.Operation is PaymentOperation.Refund)
            .ToList();

        Assert.Equal([1, 2], refunds.Select(attempt => attempt.AttemptNumber));
    }

    /// <summary>
    /// Provider detail is truncated rather than unbounded. An exception's <c>ToString()</c> can
    /// carry several kilobytes of stack trace, and a payment under retry would write several of
    /// them — the lesson of BUGS-AND-LESSONS §9, where the outbox payload column was two fields
    /// from breaking every integration event.
    /// </summary>
    [Fact]
    public void AttemptDetailIsBoundedSoItFitsItsColumn()
    {
        var payment = NewPayment();

        payment.RecordAttempt(
            PaymentOperation.Authorize,
            AttemptOutcome.TransportFailure,
            "corr",
            null,
            null,
            new string('x', PaymentAttempt.MaxDetailLength * 3),
            5,
            Now);

        Assert.Equal(PaymentAttempt.MaxDetailLength, payment.Attempts.Single().Detail!.Length);
    }

    // ------------------------------------------------------------------ refunds

    [Fact]
    public void AFullRefundLeavesThePaymentRefunded()
    {
        var payment = CapturedPayment(100.00m);

        var refund = payment.RecordRefund(Usd(100.00m), Reference("re_1"), "returned", "k1", Now);

        Assert.False(refund.IsError);
        Assert.Equal(PaymentStatusValue.Refunded, payment.Status.Value);
        Assert.Equal(100.00m, payment.RefundedAmount.Amount);
        Assert.True(payment.RefundsAreConsistent());
    }

    /// <summary>
    /// Partial refunds compose, and the stored total tracks the rows. The same state-versus-log
    /// pairing as stock, credentials, promotion redemptions and loyalty points.
    /// </summary>
    [Fact]
    public void PartialRefundsComposeUpToTheCapturedAmount()
    {
        var payment = CapturedPayment(100.00m);

        Assert.False(payment.RecordRefund(Usd(30.00m), Reference("re_1"), "part", "k1", Now).IsError);
        Assert.Equal(PaymentStatusValue.PartiallyRefunded, payment.Status.Value);
        Assert.Equal(30.00m, payment.RefundedAmount.Amount);

        Assert.False(payment.RecordRefund(Usd(20.00m), Reference("re_2"), "part", "k2", Now).IsError);
        Assert.Equal(PaymentStatusValue.PartiallyRefunded, payment.Status.Value);
        Assert.Equal(50.00m, payment.RefundedAmount.Amount);

        Assert.False(payment.RecordRefund(Usd(50.00m), Reference("re_3"), "rest", "k3", Now).IsError);
        Assert.Equal(PaymentStatusValue.Refunded, payment.Status.Value);
        Assert.Equal(100.00m, payment.RefundedAmount.Amount);

        Assert.Equal(3, payment.Refunds.Count);
        Assert.True(payment.RefundsAreConsistent());
    }

    /// <summary>
    /// <b>A refund can never exceed what was captured</b>, and the cap is summed from the rows
    /// rather than read from the counter.
    ///
    /// <para>
    /// The lesson of BUGS-AND-LESSONS §13 applied to money instead of stock: a caller that
    /// decides how much it may give back is a caller being trusted with somebody else's money.
    /// </para>
    /// </summary>
    [Fact]
    public void ARefundCannotExceedWhatWasCaptured()
    {
        var payment = CapturedPayment(100.00m);

        var tooMuch = payment.RecordRefund(Usd(150.00m), Reference("re_1"), "oops", "k1", Now);

        Assert.True(tooMuch.IsError);
        Assert.Equal("Payment.RefundExceedsCapture", tooMuch.FirstError.Code);
        Assert.Equal(0m, payment.RefundedAmount.Amount);
        Assert.Empty(payment.Refunds);
    }

    [Fact]
    public void RefundsCannotExceedTheCapturedAmountInAggregate()
    {
        var payment = CapturedPayment(100.00m);

        Assert.False(payment.RecordRefund(Usd(60.00m), Reference("re_1"), "part", "k1", Now).IsError);

        var overTheLine = payment.RecordRefund(Usd(50.00m), Reference("re_2"), "part", "k2", Now);

        Assert.True(overTheLine.IsError);
        Assert.Equal(60.00m, payment.RefundedAmount.Amount);
        Assert.True(payment.RefundsAreConsistent());
    }

    /// <summary>
    /// Idempotent on the refund's own key, so a redelivered webhook or a double-clicked admin
    /// button refunds once. The unique index on <c>(PaymentId, IdempotencyKey)</c> is the
    /// guarantee behind this read.
    /// </summary>
    [Fact]
    public void TheSameRefundKeyRefundsOnce()
    {
        var payment = CapturedPayment(100.00m);

        Assert.False(payment.RecordRefund(Usd(40.00m), Reference("re_1"), "part", "same-key", Now).IsError);

        var again = payment.RecordRefund(Usd(40.00m), Reference("re_1"), "part", "same-key", Now);

        Assert.True(again.IsError);
        Assert.Equal("Payment.RefundAlreadyRecorded", again.FirstError.Code);
        Assert.Equal(40.00m, payment.RefundedAmount.Amount);
        Assert.Single(payment.Refunds);
    }

    [Fact]
    public void ARefundNeedsItsOwnIdempotencyKey()
    {
        var payment = CapturedPayment();

        Assert.Equal(
            "Payment.RefundKeyRequired",
            payment.RecordRefund(Usd(10m), Reference("re_1"), "part", "  ", Now).FirstError.Code);
    }

    /// <summary>A refund in another currency cannot be applied, rather than being converted.</summary>
    [Fact]
    public void ARefundInAnotherCurrencyIsRefused()
    {
        var payment = CapturedPayment(100.00m);

        var wrong = payment.RecordRefund(
            Money.Create(50m, "AED").Value, Reference("re_1"), "part", "k1", Now);

        Assert.True(wrong.IsError);
        Assert.Equal("Payment.CurrencyMismatch", wrong.FirstError.Code);
    }

    [Fact]
    public void AnUncapturedPaymentCannotBeRefunded()
    {
        var payment = NewPayment();

        Assert.True(payment.RecordRefund(Usd(10m), Reference("re_1"), "x", "k1", Now).IsError);
    }

    // ------------------------------------------------------------------ chargebacks

    [Fact]
    public void AChargebackRequiresTheProvidersDisputeReference()
    {
        var payment = CapturedPayment();

        // Without it the chargeback cannot be defended, which is M4's consequence again.
        Assert.Equal(
            "Payment.DisputeReferenceRequired",
            payment.RecordChargeback("  ", Now).FirstError.Code);

        Assert.False(payment.RecordChargeback("dp_12345", Now).IsError);
        Assert.Equal(PaymentStatusValue.ChargedBack, payment.Status.Value);
    }

    // ------------------------------------------------------------------ events

    /// <summary>
    /// <b>The event chain Phase 5 completes.</b> A capture raises the event that tells Sales the
    /// order is paid, which in turn is what lets Loyalty earn — three modules, none referencing
    /// another's internals.
    ///
    /// <para>
    /// The checkout session travels on it, because that is the reference every hold in every
    /// module was placed under.
    /// </para>
    /// </summary>
    [Fact]
    public void CapturingRaisesTheEventThatTellsSalesTheOrderIsPaid()
    {
        var payment = CapturedPayment();

        var captured = Assert.Single(payment.DomainEvents.OfType<PaymentCapturedEvent>());

        Assert.Equal(payment.OrderId, captured.OrderId);
        Assert.Equal(payment.CheckoutSessionId, captured.CheckoutSessionId);
        Assert.Equal(payment.Amount.Amount, captured.Amount);
        Assert.Equal("ch_provider_1", captured.ProviderReference);
    }

    /// <summary>
    /// A decline raises the event that releases the order's holds — the first thing in the system
    /// that can mean <c>RestoreReason.PaymentFailed</c>, a value that has existed since Phase 0
    /// with nothing able to produce it.
    /// </summary>
    [Fact]
    public void DecliningRaisesTheEventThatReleasesTheOrdersHolds()
    {
        var payment = NewPayment();

        Assert.False(payment.Decline("insufficient_funds", Now).IsError);

        var declined = Assert.Single(payment.DomainEvents.OfType<PaymentDeclinedEvent>());

        Assert.Equal(payment.CheckoutSessionId, declined.CheckoutSessionId);
        Assert.Equal("insufficient_funds", declined.DeclineCode);
    }

    /// <summary>
    /// <b>The failure event carries both flags a consumer needs to decide safely.</b>
    ///
    /// <para>
    /// Releasing an order's holds on an unknown outcome would release stock for an order that may
    /// in fact have been paid — an oversell caused by cleaning up. So the only safe compensation
    /// trigger is <c>AttemptsExhausted &amp;&amp; !OutcomeIsUnknown</c>, and both halves have to
    /// travel for a consumer to apply it.
    /// </para>
    /// </summary>
    [Fact]
    public void TheFailureEventSaysBothWhetherItIsFinishedAndWhetherTheOutcomeIsKnown()
    {
        var unknown = NewPayment();
        Assert.False(unknown.Fail(outcomeIsUnknown: true, Now).IsError);

        var first = unknown.DomainEvents.OfType<PaymentFailedEvent>().Single();
        Assert.True(first.OutcomeIsUnknown);
        Assert.False(first.AttemptsExhausted);

        var known = NewPayment();

        for (var attempt = 0; attempt < Payment.MaxAttempts; attempt++)
        {
            Assert.False(known.Fail(outcomeIsUnknown: false, Now).IsError);
        }

        var last = known.DomainEvents.OfType<PaymentFailedEvent>().Last();
        Assert.False(last.OutcomeIsUnknown);
        Assert.True(last.AttemptsExhausted);
    }

    // ------------------------------------------------------------------ the integrity invariant

    /// <summary>
    /// The invariant <c>payments.integrity-check</c> asserts.
    ///
    /// <para>
    /// Both clauses, and the second is the one with teeth. BUGS-AND-LESSONS §13 is why:
    /// agreement between a counter and its log proves they are <i>consistent</i>, not that either
    /// is <i>right</i>. A refunded total above the captured amount is money leaving that never
    /// arrived.
    /// </para>
    /// </summary>
    [Fact]
    public void TheRefundedTotalAlwaysEqualsItsRowsAndNeverExceedsTheCapture()
    {
        var payment = CapturedPayment(75.50m);

        Assert.True(payment.RefundsAreConsistent());

        Assert.False(payment.RecordRefund(Usd(25.25m), Reference("re_1"), "part", "k1", Now).IsError);
        Assert.True(payment.RefundsAreConsistent());

        Assert.False(payment.RecordRefund(Usd(50.25m), Reference("re_2"), "rest", "k2", Now).IsError);
        Assert.True(payment.RefundsAreConsistent());

        Assert.Equal(75.50m, payment.RefundedAmount.Amount);
        Assert.Equal(PaymentStatusValue.Refunded, payment.Status.Value);
    }
}
