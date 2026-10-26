using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Notifications.Domain.NotificationAggregate;

/// <summary>
/// What this notification is about. <b>One value per thing this system actually tells a customer
/// — no more.</b>
///
/// <para>
/// Every entry here corresponds to an integration event some module already publishes and a
/// consumer in this module that subscribes to it. That is <b>A32</b>'s lesson applied at the
/// design stage rather than discovered afterwards: Onyx's CSV import was <i>implemented and
/// tested</i> with no endpoint, command or job calling it, while the README advertised it as
/// delivered. A notification kind with no consumer would be the same mistake — a capability that
/// exists in an enum and nowhere else.
/// </para>
/// </summary>
public enum NotificationKind
{
    /// <summary>Confirm an address before the account can check out. Carries a token.</summary>
    EmailVerification = 1,

    /// <summary>A reset link. Carries a token.</summary>
    PasswordReset = 2,

    /// <summary>
    /// A password changed. Sent to the account owner even when they changed it themselves,
    /// because the case that matters is the one where they did not — this is how a successful
    /// credential-stuffing attempt reaches the only person able to act on it.
    /// </summary>
    PasswordChanged = 3,

    /// <summary>
    /// The order exists and payment is pending. <b>Not</b> "payment received" — Onyx <b>A9</b>
    /// dispatched its payment notification before the payment row was added to the context, so a
    /// customer could be told they had paid for a transaction that then rolled back.
    /// </summary>
    OrderPlaced = 4,

    /// <summary>The money moved. The notification Onyx sent first and could not justify.</summary>
    PaymentReceived = 5,

    /// <summary>
    /// The card was refused. The one notification with an action in it, which is why the decline
    /// code travels: "try another card" and "call your bank" are different instructions.
    /// </summary>
    PaymentDeclined = 6,

    /// <summary>It is on its way. The single most-expected message in retail.</summary>
    OrderShipped = 7,

    /// <summary>It arrived.</summary>
    OrderDelivered = 8,

    /// <summary>It will not be arriving, and why.</summary>
    OrderCancelled = 9,

    /// <summary>Money is coming back.</summary>
    RefundIssued = 10,

    /// <summary>Points were credited. The reward is worthless if nobody is told it exists.</summary>
    PointsEarned = 11,

    /// <summary>Points are about to lapse, or have.</summary>
    PointsExpired = 12
}

/// <summary>How a notification reaches someone.</summary>
public enum NotificationChannel
{
    Email = 1,

    /// <summary>
    /// Declared and <b>not implemented</b>, deliberately and visibly.
    ///
    /// <para>
    /// There is no SMS sender, no SMS template and no consumer that selects this channel, and
    /// <c>NotificationTemplate.For</c> returns an error for it rather than falling through to
    /// email. An enum value with no implementation is only a lie if something can reach it — this
    /// one cannot, and the test <c>EveryChannelEitherRendersOrRefuses</c> says so.
    /// </para>
    ///
    /// <para>
    /// It is here because the channel is the one dimension of this model that will certainly grow,
    /// and because the alternative — pretending the concept does not exist — is what makes adding
    /// it later a schema migration instead of a template. Onyx <b>A45</b> is speculative
    /// generality; a single enum value that refuses is a seam, not a framework.
    /// </para>
    /// </summary>
    Sms = 2
}

/// <summary>
/// Where a notification is going.
///
/// <para>
/// Notifications has its own address type rather than reusing Customers' <c>EmailAddress</c>,
/// which is internal to that module and correctly so. The validation here is deliberately
/// <b>weaker</b>: Customers owns what a registerable address is, and this type only needs to know
/// that there is something to hand a provider. Re-implementing the stricter rule would create a
/// second authority on email validity that could disagree with the first.
/// </para>
/// </summary>
public sealed class NotificationAddress : ValueObject
{
    public const int MaxLength = 256;

    public string Value { get; }

    public string? DisplayName { get; }

    private NotificationAddress(string value, string? displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    public static ErrorOr<NotificationAddress> Create(string? value, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Error.Validation(
                "NotificationAddress.Required", "A notification must have somewhere to go.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
        {
            return Error.Validation(
                "NotificationAddress.TooLong",
                $"An address is at most {MaxLength} characters.");
        }

        // The weakest check that is still meaningful: one @ with something either side. Not an
        // RFC 5322 parser, and not a copy of Customers' rule — see the type remarks.
        var at = trimmed.IndexOf('@');

        return at > 0 && at < trimmed.Length - 1 && trimmed.LastIndexOf('@') == at
            ? new NotificationAddress(trimmed, string.IsNullOrWhiteSpace(displayName)
                ? null
                : displayName.Trim())
            : Error.Validation(
                "NotificationAddress.Malformed", $"'{trimmed}' is not a usable address.");
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() =>
        DisplayName is null ? Value : $"{DisplayName} <{Value}>";
}
