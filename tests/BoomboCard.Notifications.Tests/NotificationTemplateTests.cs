using BoomboCard.Notifications.Domain.NotificationAggregate;
using BoomboCard.Notifications.Domain.Templates;

namespace BoomboCard.Notifications.Tests;

/// <summary>
/// Every message this system sends, asserted.
///
/// <para>
/// <b>Onyx's <c>SendConfirmationEmail</c> could not be tested at all</b>, which is most of why
/// <b>A8</b> happened: the method was unreachable, its body threw
/// <c>NotImplementedException</c>, and the only call site discarded the task. There was nothing to
/// assert and no way to assert it.
/// </para>
///
/// <para>
/// Making the renderer a pure static function is what makes this file possible — the same
/// reasoning as <c>QuoteCalculator</c>, and the same payoff: the content of every customer-facing
/// message is checkable without a database, a clock, a provider or a template engine.
/// </para>
/// </summary>
public class NotificationTemplateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 9, 30, 0, TimeSpan.Zero);

    private static RenderedNotification Render(
        NotificationKind kind,
        NotificationFacts? facts = null,
        NotificationChannel channel = NotificationChannel.Email)
    {
        var rendered = NotificationTemplate.For(kind, channel, facts ?? new NotificationFacts());

        Assert.False(rendered.IsError, rendered.IsError ? rendered.FirstError.Description : "");

        return rendered.Value;
    }

    // ------------------------------------------------------------------ A32: every kind renders

    /// <summary>
    /// <b>Every notification kind renders something.</b>
    ///
    /// <para>
    /// Driven from <c>Enum.GetValues</c>, so a kind added without a template fails this test. That
    /// is Onyx <b>A32</b> caught at the design stage: its CSV import was implemented, tested, and
    /// advertised in both the README and the PR description, with no endpoint, command or job
    /// calling it. A notification kind with no template is the same thing — a capability that
    /// exists in an enum and nowhere else.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryKindRendersASubjectAndABody()
    {
        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            var rendered = NotificationTemplate.For(
                kind,
                NotificationChannel.Email,
                new NotificationFacts { RecipientName = "Sam", OrderNumber = "BC-00000001" });

            Assert.False(rendered.IsError, $"{kind} has no template.");
            Assert.False(string.IsNullOrWhiteSpace(rendered.Value.Subject), $"{kind}: no subject.");
            Assert.False(string.IsNullOrWhiteSpace(rendered.Value.Body), $"{kind}: no body.");
        }
    }

    /// <summary>
    /// <b>Every channel either renders or refuses out loud.</b>
    ///
    /// <para>
    /// <c>Sms</c> is declared with no sender, no templates and no consumer that selects it. The
    /// difference between that being a seam and it being <b>A32</b> is whether it fails loudly —
    /// so it returns an error rather than falling through to the email text, and this asserts it.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryChannelEitherRendersOrRefuses()
    {
        foreach (var channel in Enum.GetValues<NotificationChannel>())
        {
            var rendered = NotificationTemplate.For(
                NotificationKind.OrderPlaced, channel, new NotificationFacts());

            if (channel is NotificationChannel.Email)
            {
                Assert.False(rendered.IsError);
            }
            else
            {
                Assert.True(rendered.IsError, $"{channel} has no implementation and must refuse.");
                Assert.Equal("Notification.ChannelNotImplemented", rendered.FirstError.Code);
            }
        }
    }

    /// <summary>
    /// A template never throws on a missing fact. Every field is optional and every message states
    /// what it can — because a template that threw would turn a cosmetic gap into a dead-lettered
    /// notification, and a dead letter means a customer was told nothing at all.
    /// </summary>
    [Fact]
    public void NoTemplateThrowsOrFailsOnEmptyFacts()
    {
        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            var rendered = NotificationTemplate.For(
                kind, NotificationChannel.Email, new NotificationFacts());

            Assert.False(rendered.IsError, $"{kind} failed with no facts supplied.");
        }
    }

    // ------------------------------------------------------------------ the secret flag

    /// <summary>
    /// <b>Exactly the two kinds that carry a token are marked as secret.</b>
    ///
    /// <para>
    /// That flag is load-bearing rather than informational: it is what makes the logging sender
    /// redact the body, the retention sweep discard it, and the query projections refuse to return
    /// it. A kind marked wrongly in either direction is a real defect — too few and a live reset
    /// link ends up in a log aggregator, too many and an order confirmation is deleted an hour
    /// after it is sent.
    /// </para>
    /// </summary>
    [Fact]
    public void OnlyTheKindsThatCarryATokenAreMarkedSecret()
    {
        var expected = new[] { NotificationKind.EmailVerification, NotificationKind.PasswordReset };

        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            var rendered = Render(kind, new NotificationFacts { Link = "https://x/y?token=abc" });

            Assert.Equal(expected.Contains(kind), rendered.ContainsSecret);
        }
    }

    /// <summary>And the two that are marked actually contain the link, or the flag is a lie.</summary>
    [Theory]
    [InlineData(NotificationKind.EmailVerification)]
    [InlineData(NotificationKind.PasswordReset)]
    public void ASecretBodyActuallyContainsTheLink(NotificationKind kind)
    {
        var rendered = Render(
            kind,
            new NotificationFacts
            {
                Link = "https://shop.example/verify-email?token=ABCDEF",
                ExpiresAtUtc = Now
            });

        Assert.True(rendered.ContainsSecret);
        Assert.Contains("https://shop.example/verify-email?token=ABCDEF", rendered.Body);
        Assert.Contains("2026-07-04 09:30 UTC", rendered.Body);
    }

    /// <summary>
    /// The subject never carries the link, which is why <c>ForgetBody</c> keeps the subject. A row
    /// with neither subject nor body records that something was sent and nothing about what.
    /// </summary>
    [Fact]
    public void ASubjectNeverCarriesTheToken()
    {
        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            var rendered = Render(kind, new NotificationFacts { Link = "https://x/y?token=SECRET" });

            Assert.DoesNotContain("SECRET", rendered.Subject);
        }
    }

    // ------------------------------------------------------------------ A9: what the words say

    /// <summary>
    /// <b>A9, as a content assertion.</b> The order confirmation says payment is still being
    /// processed and does <b>not</b> say it succeeded.
    ///
    /// <para>
    /// Onyx dispatched <c>NotifyPaymentConfirmed</c> before the payment row was even added to the
    /// context, before <c>SaveChanges</c> and before <c>Commit</c> — so a rolled-back transaction
    /// left a customer holding a confirmation for a transaction that never happened. The outbox
    /// fixes the ordering; this fixes the wording, and both matter.
    /// </para>
    /// </summary>
    [Fact]
    public void TheOrderConfirmationDoesNotClaimPaymentSucceeded()
    {
        var rendered = Render(
            NotificationKind.OrderPlaced,
            new NotificationFacts
            {
                RecipientName = "Sam",
                OrderNumber = "BC-00000042",
                Amount = 86.40m,
                CurrencyCode = "USD"
            });

        Assert.Contains("BC-00000042", rendered.Subject);
        Assert.Contains("86.40 USD", rendered.Body);
        Assert.Contains("still being processed", rendered.Body);

        // The words that would be A9 if they appeared here.
        Assert.DoesNotContain("has gone through", rendered.Body);
        Assert.DoesNotContain("Payment received", rendered.Body);
    }

    /// <summary>And the payment receipt, which is a separate message, does say it.</summary>
    [Fact]
    public void ThePaymentReceiptSaysTheMoneyMoved()
    {
        var rendered = Render(
            NotificationKind.PaymentReceived,
            new NotificationFacts
            {
                OrderNumber = "BC-00000042",
                Amount = 86.40m,
                CurrencyCode = "USD",
                Reference = "ch_abc123"
            });

        Assert.Contains("has gone through", rendered.Body);
        Assert.Contains("ch_abc123", rendered.Body);
    }

    // ------------------------------------------------------------------ M7: money in words

    /// <summary>
    /// <b>M7, where the customer would have seen it.</b> Onyx never rounded, and a real order
    /// total in its register is <c>83.096766</c> — which is also what it would have emailed.
    /// </summary>
    [Theory]
    [InlineData(83.096766, "83.10 USD")]
    [InlineData(86.4, "86.40 USD")]
    [InlineData(5, "5.00 USD")]
    [InlineData(0.005, "0.01 USD")]
    public void MoneyIsAlwaysTwoDecimalPlacesWithItsCurrency(decimal amount, string expected)
    {
        var rendered = Render(
            NotificationKind.PaymentReceived,
            new NotificationFacts { Amount = amount, CurrencyCode = "USD", OrderNumber = "BC-1" });

        Assert.Contains(expected, rendered.Body);
    }

    /// <summary>A missing amount says so rather than rendering as <c>0.00</c>, which would be a lie.</summary>
    [Fact]
    public void AMissingAmountIsNotRenderedAsZero()
    {
        var rendered = Render(
            NotificationKind.PaymentReceived, new NotificationFacts { OrderNumber = "BC-1" });

        Assert.DoesNotContain("0.00", rendered.Body);
        Assert.Contains("the order total", rendered.Body);
    }

    // ------------------------------------------------------------------ A29: time in words

    /// <summary>
    /// Every instant is stated as UTC. Onyx <b>A29</b> wrote <c>DateTime.Now</c> — server local
    /// time — into a field named <c>OrderDateUtc</c>, and <b>M17</b> compared a promotion's expiry
    /// against local midnight so the cutover moved with the deployment's timezone. An expiry in an
    /// email with no stated zone is the same ambiguity handed to the customer.
    /// </summary>
    [Fact]
    public void EveryStatedInstantNamesItsTimezone()
    {
        var rendered = Render(
            NotificationKind.PasswordReset,
            new NotificationFacts
            {
                Link = "https://x/y?token=a",
                ExpiresAtUtc = new DateTimeOffset(2026, 7, 4, 9, 30, 0, TimeSpan.FromHours(5))
            });

        // Converted to UTC and labelled, so 09:30+05:00 reads as 04:30 UTC.
        Assert.Contains("2026-07-04 04:30 UTC", rendered.Body);
    }

    // ------------------------------------------------------------------ the decline advice

    /// <summary>
    /// <b>A decline tells the customer what to do.</b> The provider's code is machine-readable;
    /// the customer needs an instruction, and <c>insufficient_funds</c> and <c>do_not_honor</c>
    /// require different ones — the second needs a phone call to somebody else entirely.
    ///
    /// <para>
    /// Onyx had no declined path anywhere in the system (<b>M1</b>), so it had nothing to tell
    /// anybody.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("insufficient_funds", "not enough funds")]
    [InlineData("expired_card", "expired")]
    [InlineData("do_not_honor", "call")]
    [InlineData(null, "did not tell us why")]
    [InlineData("something_new_the_provider_added", "did not tell us why")]
    public void ADeclineCarriesActionableAdvice(string? declineCode, string expected)
    {
        var rendered = Render(
            NotificationKind.PaymentDeclined,
            new NotificationFacts { OrderNumber = "BC-1", DeclineCode = declineCode });

        Assert.Contains(expected, rendered.Body);

        // And it never implies money was taken, because none was.
        Assert.Contains("Nothing has been charged", rendered.Body);
    }

    // ------------------------------------------------------------------ partial shipment

    /// <summary>
    /// <b>A31.</b> Onyx's order lifecycle "stops at Paid" and nothing ever set <c>Shipped</c>.
    /// Partial shipment could not be represented at all, which is why the message has to say
    /// whether more is coming — a customer who gets one box and was told "your order shipped"
    /// otherwise assumes it is complete.
    /// </summary>
    [Fact]
    public void APartialShipmentSaysMoreIsComingAndAFullOneSaysItIsNot()
    {
        var partial = Render(
            NotificationKind.OrderShipped,
            new NotificationFacts { OrderNumber = "BC-1", ItemCount = 2, PartialShipment = true });

        Assert.Contains("2 items", partial.Body);
        Assert.Contains("rest will follow", partial.Body);

        var full = Render(
            NotificationKind.OrderShipped,
            new NotificationFacts { OrderNumber = "BC-1", ItemCount = 1, PartialShipment = false });

        Assert.Contains("1 item.", full.Body);
        Assert.Contains("everything on the order", full.Body);
        Assert.DoesNotContain("rest will follow", full.Body);
    }

    // ------------------------------------------------------------------ the points messages

    /// <summary>
    /// <b>The points messages do not quote a balance, and that is deliberate.</b>
    ///
    /// <para>
    /// Neither Loyalty event carries one, and the only way to get it is
    /// <c>GetPointsBalanceQuery</c> — which takes a currency code, because a balance is reported
    /// alongside what it is worth. Notifications has no currency here, so supplying one means
    /// guessing.
    /// </para>
    ///
    /// <para>
    /// That is exactly the mistake <c>PointsRedeemedIntegrationEvent</c>'s own documentation
    /// records: its first version carried <c>DiscountAmount</c> and <c>CurrencyCode</c>, and "the
    /// only way to populate those fields was to guess a currency, which is how a figure becomes
    /// confidently wrong". A balance that is quietly wrong in an email is worse than one that is
    /// absent, because the customer will act on it.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePointsMessagesDoNotQuoteABalanceTheyCannotKnow()
    {
        var earned = Render(
            NotificationKind.PointsEarned,
            new NotificationFacts { Points = 80, OrderNumber = "BC-00000042" });

        Assert.Contains("80 points", earned.Body);
        Assert.Contains("BC-00000042", earned.Body);
        Assert.Contains("account page", earned.Body);
        Assert.DoesNotContain("balance to", earned.Body);

        var expired = Render(NotificationKind.PointsExpired, new NotificationFacts { Points = 40 });

        Assert.Contains("40 points have expired", expired.Body);
        Assert.DoesNotContain("balance at", expired.Body);
    }

    // ------------------------------------------------------------------ the password notice

    /// <summary>
    /// The security notice is addressed to the case where the owner did <b>not</b> make the
    /// change, because that is the one that matters: it is how a successful credential-stuffing
    /// attempt reaches the only person able to act on it.
    /// </summary>
    [Fact]
    public void ThePasswordChangeNoticeTellsTheOwnerWhatToDoIfItWasNotThem()
    {
        var rendered = Render(
            NotificationKind.PasswordChanged,
            new NotificationFacts { OccurredAtUtc = Now, ViaReset = true });

        Assert.Contains("using a reset link", rendered.Body);
        Assert.Contains("If that was not you", rendered.Body);
        Assert.Contains("2026-07-04 09:30 UTC", rendered.Body);
    }

    // ------------------------------------------------------------------ plain text

    /// <summary>
    /// <b>No HTML anywhere.</b> A customer name is interpolated into every one of these, and a
    /// name is user input — so plain text means there is no markup to escape and no injection
    /// surface. HTML email needs a templating engine with contextual escaping, which is a real
    /// dependency with a real attack surface, and nothing here needs it yet.
    /// </summary>
    [Fact]
    public void NoTemplateEmitsMarkupEvenWhenAnameContainsIt()
    {
        var hostile = "<script>alert('x')</script>";

        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            var rendered = Render(kind, new NotificationFacts { RecipientName = hostile });

            // The name appears verbatim — which is safe precisely because the body is never
            // interpreted as markup. What must NOT happen is a template wrapping it in tags and
            // creating the expectation that it is HTML.
            Assert.DoesNotContain("<html", rendered.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<body", rendered.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<a href", rendered.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A missing name reads as a greeting rather than as a blank or a null.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingNameFallsBackToAGreeting(string? name)
    {
        var rendered = Render(
            NotificationKind.OrderPlaced, new NotificationFacts { RecipientName = name });

        Assert.Contains("Hello there,", rendered.Body);
        Assert.DoesNotContain("Hello ,", rendered.Body);
    }
}
