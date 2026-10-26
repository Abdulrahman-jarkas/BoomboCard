using BoomboCard.Payments.Domain.PaymentAggregate;

namespace BoomboCard.Payments.Tests;

/// <summary>
/// The state machine, on its own.
///
/// <para>
/// <b>Onyx A39</b> is why this type exists: <c>Payment.Status</c> was
/// <c>public string Status { get; set; } = "Pending"</c>, only ever written as the literal
/// <c>"Paid"</c>, with no representation for declined or refunded at all — while
/// <c>OrderStatus</c> next to it was a proper enum.
/// </para>
///
/// <para>
/// Tested separately from the aggregate because the transitions are the part that must be
/// exhaustive, and a table of them reads as a specification. <c>Payment</c> then adds the rules
/// that need more than a status: a provider reference, the attempt budget, the refund
/// arithmetic.
/// </para>
/// </summary>
public class PaymentStatusTests
{
    private static PaymentStatus At(PaymentStatusValue value) => PaymentStatus.From(value);

    // ------------------------------------------------------------------ the happy paths

    [Fact]
    public void APendingPaymentCanAuthorize()
    {
        var next = PaymentStatus.Pending.Authorize();

        Assert.False(next.IsError);
        Assert.Equal(PaymentStatusValue.Authorized, next.Value.Value);
    }

    /// <summary>
    /// Both provider shapes are modelled rather than one being assumed: a provider that
    /// authorizes then captures, and one that does both in a single call.
    /// </summary>
    [Theory]
    [InlineData(PaymentStatusValue.Pending)]
    [InlineData(PaymentStatusValue.Authorized)]
    [InlineData(PaymentStatusValue.Failed)]
    public void CaptureIsReachableFromEveryStateAProviderCanCaptureFrom(PaymentStatusValue from)
    {
        var next = At(from).Capture();

        Assert.False(next.IsError);
        Assert.Equal(PaymentStatusValue.Captured, next.Value.Value);
    }

    // ------------------------------------------------------------------ M5: decline vs failure

    /// <summary>
    /// <b>The asymmetry that is M5.</b> A transport failure says nothing about the card, so it is
    /// retryable. A decline is the provider's considered answer, so it is not.
    ///
    /// <para>
    /// Onyx retried three times with no idempotency key and no distinction between the two,
    /// which means it retried declines as well as timeouts. Retrying a decline is how a
    /// customer's bank starts treating you as an attacker.
    /// </para>
    /// </summary>
    [Fact]
    public void AFailureIsRetryableAndADeclineIsNot()
    {
        Assert.True(At(PaymentStatusValue.Failed).CanSubmit);
        Assert.False(At(PaymentStatusValue.Declined).CanSubmit);
    }

    [Fact]
    public void ADeclinedPaymentCannotBeSubmittedAgain()
    {
        var again = At(PaymentStatusValue.Declined).Authorize();

        Assert.True(again.IsError);
        Assert.Equal("Payment.AlreadyDeclined", again.FirstError.Code);
    }

    /// <summary>
    /// A <c>Failed</c> payment is deliberately <b>not</b> terminal: an answer may still arrive,
    /// from a webhook or from the reconciliation job. Closing the record would be Onyx's mistake
    /// — treating "no answer" as an answer — in the opposite direction.
    /// </summary>
    [Fact]
    public void AFailedPaymentIsNotTerminal()
    {
        Assert.False(At(PaymentStatusValue.Failed).IsTerminal);
        Assert.True(At(PaymentStatusValue.Declined).IsTerminal);
        Assert.True(At(PaymentStatusValue.Cancelled).IsTerminal);
        Assert.True(At(PaymentStatusValue.Refunded).IsTerminal);
        Assert.True(At(PaymentStatusValue.ChargedBack).IsTerminal);
    }

    // ------------------------------------------------------------------ M3: once only

    [Fact]
    public void ACapturedPaymentCannotBeCapturedAgain()
    {
        var again = At(PaymentStatusValue.Captured).Capture();

        Assert.True(again.IsError);
        Assert.Equal("Payment.AlreadyCaptured", again.FirstError.Code);
    }

    [Fact]
    public void AnAuthorizedPaymentCannotAuthorizeAgain()
    {
        var again = At(PaymentStatusValue.Authorized).Authorize();

        Assert.True(again.IsError);
        Assert.Equal("Payment.AlreadyAuthorized", again.FirstError.Code);
    }

    /// <summary>
    /// A provider does not decline a payment it has already captured, so a notification claiming
    /// it is refused rather than applied. That is either a bug or an attack, and in both cases
    /// the right answer is no.
    /// </summary>
    [Theory]
    [InlineData(PaymentStatusValue.Captured)]
    [InlineData(PaymentStatusValue.Refunded)]
    [InlineData(PaymentStatusValue.PartiallyRefunded)]
    [InlineData(PaymentStatusValue.ChargedBack)]
    [InlineData(PaymentStatusValue.Cancelled)]
    public void ASettledOrClosedPaymentCannotBeDeclined(PaymentStatusValue from)
    {
        Assert.True(At(from).Decline().IsError);
    }

    // ------------------------------------------------------------------ settlement

    /// <summary>
    /// <c>IsSettled</c> is the predicate <c>Payment.Authorize</c> and <c>Payment.Capture</c> hang
    /// the provider-reference requirement on, so which statuses it covers is load-bearing rather
    /// than cosmetic.
    /// </summary>
    [Theory]
    [InlineData(PaymentStatusValue.Authorized, true)]
    [InlineData(PaymentStatusValue.Captured, true)]
    [InlineData(PaymentStatusValue.PartiallyRefunded, true)]
    [InlineData(PaymentStatusValue.Refunded, true)]
    [InlineData(PaymentStatusValue.ChargedBack, true)]
    [InlineData(PaymentStatusValue.Pending, false)]
    [InlineData(PaymentStatusValue.Declined, false)]
    [InlineData(PaymentStatusValue.Failed, false)]
    [InlineData(PaymentStatusValue.Cancelled, false)]
    public void OnlyStatusesWithProviderMoneyBehindThemCountAsSettled(
        PaymentStatusValue status,
        bool expected)
    {
        Assert.Equal(expected, At(status).IsSettled);
    }

    // ------------------------------------------------------------------ refunds

    [Fact]
    public void APartialRefundLeavesThePaymentPartiallyRefundedAndAFullOneRefunded()
    {
        var partial = At(PaymentStatusValue.Captured).Refund(fully: false);
        var full = At(PaymentStatusValue.Captured).Refund(fully: true);

        Assert.Equal(PaymentStatusValue.PartiallyRefunded, partial.Value.Value);
        Assert.Equal(PaymentStatusValue.Refunded, full.Value.Value);
    }

    /// <summary>A partially refunded payment can be refunded again, up to the remainder.</summary>
    [Fact]
    public void APartiallyRefundedPaymentCanBeRefundedAgain()
    {
        var next = At(PaymentStatusValue.PartiallyRefunded).Refund(fully: true);

        Assert.False(next.IsError);
        Assert.Equal(PaymentStatusValue.Refunded, next.Value.Value);
    }

    /// <summary>
    /// An authorization has taken no money, so there is nothing to give back. The error says so
    /// rather than reporting a generic conflict — a reversal of an authorization is a different
    /// operation with a different provider call.
    /// </summary>
    [Fact]
    public void AnAuthorizedButUncapturedPaymentCannotBeRefunded()
    {
        var next = At(PaymentStatusValue.Authorized).Refund(fully: true);

        Assert.True(next.IsError);
        Assert.Equal("Payment.NotCaptured", next.FirstError.Code);
    }

    [Fact]
    public void AFullyRefundedPaymentCannotBeRefundedAgain()
    {
        Assert.Equal(
            "Payment.AlreadyRefunded",
            At(PaymentStatusValue.Refunded).Refund(fully: true).FirstError.Code);
    }

    // ------------------------------------------------------------------ chargebacks

    /// <summary>
    /// Reachable from any settled status, <b>including a refunded one</b>. A dispute can land
    /// after a partial refund, and refusing to record it would mean our data disagreed with the
    /// bank statement — which is the one thing a payment record exists to avoid.
    /// </summary>
    [Theory]
    [InlineData(PaymentStatusValue.Authorized)]
    [InlineData(PaymentStatusValue.Captured)]
    [InlineData(PaymentStatusValue.PartiallyRefunded)]
    [InlineData(PaymentStatusValue.Refunded)]
    public void AnySettledPaymentCanBeChargedBack(PaymentStatusValue from)
    {
        var next = At(from).ChargeBack();

        Assert.False(next.IsError);
        Assert.Equal(PaymentStatusValue.ChargedBack, next.Value.Value);
    }

    [Theory]
    [InlineData(PaymentStatusValue.Pending)]
    [InlineData(PaymentStatusValue.Declined)]
    [InlineData(PaymentStatusValue.Failed)]
    [InlineData(PaymentStatusValue.Cancelled)]
    public void APaymentThatNeverSettledCannotBeChargedBack(PaymentStatusValue from)
    {
        Assert.True(At(from).ChargeBack().IsError);
    }

    [Fact]
    public void APaymentCannotBeChargedBackTwice()
    {
        Assert.Equal(
            "Payment.AlreadyChargedBack",
            At(PaymentStatusValue.ChargedBack).ChargeBack().FirstError.Code);
    }

    // ------------------------------------------------------------------ cancellation

    /// <summary>
    /// <c>Pending</c> only. Once a provider has been asked, its answer decides — cancelling would
    /// be us overruling it, and the money may already have moved.
    /// </summary>
    [Fact]
    public void OnlyAPendingPaymentCanBeCancelled()
    {
        Assert.False(PaymentStatus.Pending.Cancel().IsError);

        foreach (var status in new[]
                 {
                     PaymentStatusValue.Authorized,
                     PaymentStatusValue.Captured,
                     PaymentStatusValue.Declined,
                     PaymentStatusValue.Failed,
                     PaymentStatusValue.Refunded
                 })
        {
            Assert.True(At(status).Cancel().IsError, $"{status} should not be cancellable.");
        }
    }

    // ------------------------------------------------------------------ exhaustiveness

    /// <summary>
    /// <b>Every status has defined behaviour for every transition.</b>
    ///
    /// <para>
    /// Not an assertion about outcomes — it is an assertion that nothing throws, returns null, or
    /// falls through a <c>switch</c> without a case. Onyx <b>M1</b> is exactly a missing case: the
    /// code had no branch for "the provider did not say yes", so the one path it did have ran
    /// regardless.
    /// </para>
    ///
    /// <para>
    /// A new status added to the enum without being considered here fails this test, which is the
    /// point of driving it from <c>Enum.GetValues</c> rather than a list.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryTransitionIsDefinedForEveryStatus()
    {
        foreach (var value in Enum.GetValues<PaymentStatusValue>())
        {
            var status = At(value);

            // Each returns either a status or a named error. Neither may throw, and neither may
            // produce a null value on the success path.
            foreach (var (name, result) in new (string, ErrorOr.ErrorOr<PaymentStatus>)[]
                     {
                         ("Authorize", status.Authorize()),
                         ("Capture", status.Capture()),
                         ("Decline", status.Decline()),
                         ("Fail", status.Fail()),
                         ("Refund(full)", status.Refund(true)),
                         ("Refund(partial)", status.Refund(false)),
                         ("ChargeBack", status.ChargeBack()),
                         ("Cancel", status.Cancel())
                     })
            {
                if (result.IsError)
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(result.FirstError.Code),
                        $"{value}.{name} returned an error with no code.");
                }
                else
                {
                    Assert.NotNull(result.Value);
                }
            }
        }
    }
}
