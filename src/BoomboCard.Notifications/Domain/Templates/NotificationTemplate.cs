using BoomboCard.Notifications.Domain.NotificationAggregate;
using ErrorOr;

namespace BoomboCard.Notifications.Domain.Templates;

/// <summary>
/// A subject and a body, ready to hand to a provider.
///
/// <para>
/// <b>Whether this contains a secret is a property of the notification kind, and it travels with
/// the text.</b> A verification or reset body holds the plaintext token — the database stores
/// only a hash, so the email is the single thing in existence that can deliver it. That makes
/// <see cref="ContainsSecret"/> load-bearing rather than informational: it is what stops the
/// body being logged, projected into a response, or kept after delivery.
/// </para>
/// </summary>
public sealed record RenderedNotification(string Subject, string Body, bool ContainsSecret);

/// <summary>
/// Turns a notification kind and its facts into a subject and a body.
///
/// <para>
/// <b>Pure static, no I/O, no clock, no template engine.</b> The same shape as
/// <c>QuoteCalculator</c>, and for the same reason: every message in the system can then be
/// asserted in a unit test, and Onyx's <c>SendConfirmationEmail</c> could not be exercised at all
/// because it was an unreachable method that threw (<b>A8</b>).
/// </para>
///
/// <para>
/// <b>Rendered at enqueue, not at send — and that is the decision worth defending.</b> The facts
/// arrive on an integration event; the send may happen seconds later or, after four failed
/// attempts and backoff, several hours later. Rendering late would mean reading the order, the
/// payment and the points balance again at send time, and a retried "your order shipped" email
/// would describe the order as it is <i>now</i> rather than as it was when it shipped. Freezing
/// the text at enqueue makes a retry byte-identical to the first attempt, which is the only
/// version of a retry a recipient can make sense of.
/// </para>
///
/// <para>
/// It is the same reasoning as <c>Order.PlaceFrom</c> copying the quote verbatim instead of
/// recomputing it: when the record and the thing it describes can drift, copy at the moment they
/// agreed.
/// </para>
///
/// <para>
/// <b>Plain text, deliberately.</b> No HTML, so there is no markup to escape and no injection
/// surface — a customer name is interpolated into these strings, and a name is user input. HTML
/// email needs a templating engine with contextual escaping, which is a real dependency with a
/// real attack surface, and nothing here needs it yet.
/// </para>
/// </summary>
public static class NotificationTemplate
{
    /// <summary>How a message signs off. One place, so it cannot drift between messages.</summary>
    private const string Signature = "\n\n— BoomboCard";

    /// <summary>
    /// Renders one notification.
    ///
    /// <para>
    /// Every arm of the switch is covered, and <c>NotificationChannel.Sms</c> returns an error
    /// rather than falling through to the email text. A channel with no implementation refusing
    /// out loud is the difference between a seam and <b>A32</b>.
    /// </para>
    /// </summary>
    public static ErrorOr<RenderedNotification> For(
        NotificationKind kind,
        NotificationChannel channel,
        NotificationFacts facts)
    {
        if (channel is not NotificationChannel.Email)
        {
            return Error.Validation(
                "Notification.ChannelNotImplemented",
                $"The {channel} channel has no sender, no templates and no consumer that selects "
                + "it. It is declared as a seam and refuses rather than silently rendering as "
                + "email.");
        }

        var name = string.IsNullOrWhiteSpace(facts.RecipientName) ? "there" : facts.RecipientName;

        return kind switch
        {
            NotificationKind.EmailVerification => Secret(
                "Confirm your email address",
                $"Hello {name},\n\n"
                + "Confirm your email address to finish setting up your BoomboCard account.\n\n"
                + $"    {facts.Link}\n\n"
                + $"The link stops working at {Instant(facts.ExpiresAtUtc)}. If you did not "
                + "create an account, ignore this message — nothing happens until the link is "
                + "used."),

            NotificationKind.PasswordReset => Secret(
                "Reset your password",
                $"Hello {name},\n\n"
                + "Someone asked to reset the password on your BoomboCard account.\n\n"
                + $"    {facts.Link}\n\n"
                + $"The link stops working at {Instant(facts.ExpiresAtUtc)} and can be used "
                + "once. If this was not you, you can ignore this message — your password has "
                + "not changed."),

            // Sent whether or not the owner made the change, because the case that matters is
            // the one where they did not.
            NotificationKind.PasswordChanged => Plain(
                "Your password was changed",
                $"Hello {name},\n\n"
                + $"The password on your BoomboCard account was changed at "
                + $"{Instant(facts.OccurredAtUtc)}"
                + (facts.ViaReset ? ", using a reset link" : string.Empty)
                + ".\n\n"
                + "If that was not you, reset your password now and contact us — somebody else "
                + "has your current one."),

            // "Received", not "paid". A9: Onyx told customers their payment had succeeded before
            // the payment row was added to the context.
            NotificationKind.OrderPlaced => Plain(
                $"We have your order {facts.OrderNumber}",
                $"Hello {name},\n\n"
                + $"We have received order {facts.OrderNumber} for "
                + $"{Money(facts.Amount, facts.CurrencyCode)}.\n\n"
                + "Payment is still being processed. We will email you again once it clears, and "
                + "once more when the order ships."),

            NotificationKind.PaymentReceived => Plain(
                $"Payment received for order {facts.OrderNumber}",
                $"Hello {name},\n\n"
                + $"Your payment of {Money(facts.Amount, facts.CurrencyCode)} for order "
                + $"{facts.OrderNumber} has gone through.\n\n"
                + $"Payment reference: {facts.Reference}\n\n"
                + "We will email you again when it ships."),

            // The one notification with an instruction in it, which is why the code travels.
            NotificationKind.PaymentDeclined => Plain(
                $"We could not take payment for order {facts.OrderNumber}",
                $"Hello {name},\n\n"
                + $"Your card was declined for order {facts.OrderNumber}.\n\n"
                + $"{AdviceFor(facts.DeclineCode)}\n\n"
                + "Nothing has been charged, and the order is being held for now. You can pay "
                + "with another card from your account."),

            NotificationKind.OrderShipped => Plain(
                $"Order {facts.OrderNumber} is on its way",
                $"Hello {name},\n\n"
                + $"Order {facts.OrderNumber} has shipped"
                + (facts.ItemCount > 0
                    ? $" — {facts.ItemCount} item{(facts.ItemCount == 1 ? string.Empty : "s")}"
                    : string.Empty)
                + ".\n\n"
                + (facts.PartialShipment
                    ? "This is part of your order; the rest will follow separately and we will "
                      + "email you again when it goes out."
                    : "That is everything on the order.")),

            NotificationKind.OrderDelivered => Plain(
                $"Order {facts.OrderNumber} has arrived",
                $"Hello {name},\n\n"
                + $"Order {facts.OrderNumber} was delivered.\n\n"
                + "If anything is wrong with it, reply to this message and we will sort it out."),

            NotificationKind.OrderCancelled => Plain(
                $"Order {facts.OrderNumber} has been cancelled",
                $"Hello {name},\n\n"
                + $"Order {facts.OrderNumber} has been cancelled"
                + (string.IsNullOrWhiteSpace(facts.Reason) ? string.Empty : $": {facts.Reason}")
                + ".\n\n"
                + "Nothing further will be charged. Anything already paid is being refunded, and "
                + "you will get a separate message when that is done."),

            NotificationKind.RefundIssued => Plain(
                $"Refund issued for order {facts.OrderNumber}",
                $"Hello {name},\n\n"
                + $"We have refunded {Money(facts.Amount, facts.CurrencyCode)} against order "
                + $"{facts.OrderNumber}.\n\n"
                + $"Refund reference: {facts.Reference}\n\n"
                + "How long it takes to appear depends on your bank — usually a few working "
                + "days."),

            // NOTE what these two do NOT say: the resulting balance.
            //
            // Neither Loyalty event carries one, and the only way to get it is
            // GetPointsBalanceQuery — which takes a currency code, because a balance is reported
            // alongside what it is worth. Notifications has no currency here, so supplying one
            // would mean guessing.
            //
            // That is exactly the mistake PointsRedeemedIntegrationEvent's own documentation
            // records: its first version carried DiscountAmount and CurrencyCode, and "the only
            // way to populate those fields was to guess a currency, which is how a figure becomes
            // confidently wrong". A balance that is quietly wrong in an email is worse than one
            // that is absent, because the customer will act on it.
            //
            // So these say what the event actually knows, and point at the place that owns the
            // rest.
            NotificationKind.PointsEarned => Plain(
                "You have earned points",
                $"Hello {name},\n\n"
                + $"You earned {facts.Points} points"
                + (string.IsNullOrWhiteSpace(facts.OrderNumber)
                    ? string.Empty
                    : $" on order {facts.OrderNumber}")
                + ".\n\n"
                + "Points come off your next order automatically at checkout. Your balance and "
                + "what it is worth are on your account page."),

            NotificationKind.PointsExpired => Plain(
                "Some of your points have expired",
                $"Hello {name},\n\n"
                + $"{facts.Points} points have expired.\n\n"
                + "Points last two years from the day they are earned. Your remaining balance is "
                + "on your account page."),

            _ => Error.Validation(
                "Notification.NoTemplate",
                $"Notification kind '{kind}' has no template, so nothing is sent. A kind with no "
                + "template would be a capability that exists in an enum and nowhere else.")
        };
    }

    /// <summary>
    /// What to tell a customer about a decline. The provider's code is machine-readable and the
    /// customer needs an instruction — <c>insufficient_funds</c> and <c>expired_card</c> require
    /// different actions, and <c>do_not_honor</c> requires a phone call to somebody else.
    ///
    /// <para>
    /// The same mapping the submit endpoint returns, phrased for an email rather than a UI.
    /// Deliberately duplicated rather than shared: a module reaching into Payments for its
    /// customer-facing copy would couple the two on wording, and Payments is not the authority on
    /// what an email says.
    /// </para>
    /// </summary>
    private static string AdviceFor(string? declineCode) =>
        declineCode switch
        {
            "insufficient_funds" =>
                "The bank said there were not enough funds available. Trying another card is "
                + "usually the quickest fix.",
            "expired_card" =>
                "The card has expired. Check the expiry date, or use a different card.",
            "do_not_honor" =>
                "The bank declined it without telling us why — this usually needs a call to "
                + "them, or a different card.",
            _ =>
                "The bank did not tell us why. Trying another card is usually the quickest fix."
        };

    private static RenderedNotification Plain(string subject, string body) =>
        new(subject, body + Signature, ContainsSecret: false);

    private static RenderedNotification Secret(string subject, string body) =>
        new(subject, body + Signature, ContainsSecret: true);

    /// <summary>
    /// UTC, stated as UTC. Onyx <b>A29</b> wrote <c>DateTime.Now</c> — server local time — into a
    /// field named <c>OrderDateUtc</c>, and <b>M17</b> compared a promotion's expiry against a
    /// local midnight, so the cutover moved with the deployment's timezone.
    /// </summary>
    private static string Instant(DateTimeOffset? at) =>
        at is null ? "the stated time" : $"{at.Value.UtcDateTime:yyyy-MM-dd HH:mm} UTC";

    /// <summary>
    /// Two decimal places and the currency code, always. <b>M7</b>: Onyx never rounded, and a
    /// real order total in its register is <c>83.096766</c> — which is also what the customer
    /// would have been emailed.
    /// </summary>
    private static string Money(decimal? amount, string? currencyCode) =>
        amount is null
            ? "the order total"
            : $"{amount.Value:0.00} {currencyCode ?? string.Empty}".TrimEnd();
}

/// <summary>
/// Everything any template might need, flattened.
///
/// <para>
/// One record rather than a type per kind, which is a real trade-off. A per-kind type would make
/// a missing field a compile error; this makes it a nullable that renders as "the order total".
/// The flat record wins here because the alternative is twelve near-identical records and a
/// discriminated dispatch, for templates whose fields overlap almost completely — and because
/// the consumer that builds one of these is three lines away from the template that reads it, so
/// a missing field is caught by the test that asserts the rendered text.
/// </para>
///
/// <para>
/// Every field is optional and every template states what it does without one. A template that
/// threw on a missing field would turn a cosmetic gap into a dead-lettered notification.
/// </para>
/// </summary>
public sealed record NotificationFacts
{
    public string? RecipientName { get; init; }

    /// <summary>A verification or reset URL. Carries a token, which is why it is a secret.</summary>
    public string? Link { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public DateTimeOffset? OccurredAtUtc { get; init; }

    public string? OrderNumber { get; init; }

    public decimal? Amount { get; init; }

    public string? CurrencyCode { get; init; }

    /// <summary>A payment or refund reference. Shown so a customer can quote it to support.</summary>
    public string? Reference { get; init; }

    public string? DeclineCode { get; init; }

    public string? Reason { get; init; }

    public int ItemCount { get; init; }

    public bool PartialShipment { get; init; }

    public bool ViaReset { get; init; }

    public int Points { get; init; }
}
