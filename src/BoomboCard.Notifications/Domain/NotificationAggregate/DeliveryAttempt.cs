using BoomboCard.SharedKernel;

namespace BoomboCard.Notifications.Domain.NotificationAggregate;

/// <summary>
/// What a notification provider said.
///
/// <para>
/// <b>Three outcomes, not four — and the absence of a fourth is the decision.</b> Payments has
/// <c>AttemptOutcome.Indeterminate</c> for a request that was sent and never answered, plus an
/// idempotency key, an enquiry endpoint and a reconciliation job to resolve it. None of that
/// machinery is here, and it is worth being explicit about why rather than looking like an
/// oversight.
/// </para>
///
/// <para>
/// <b>The cost of the two mistakes is not remotely symmetrical.</b> If a charge request times out
/// and we retry it, a customer is billed twice — money taken that they did not agree to, which is
/// the worst thing a retail system can do. If an email send times out and we retry it, a customer
/// gets the same email twice, which is mildly annoying. So Payments spends real design effort
/// making the unknown case resolvable, and Notifications accepts at-least-once delivery and moves
/// on.
/// </para>
///
/// <para>
/// This is the same reasoning the project applies everywhere — build the guarantee the cost of
/// being wrong justifies — and it is the reason copying Phase 5's shape wholesale would have been
/// the wrong instinct. A provider-side idempotency key for email <i>is</i> available from most
/// providers and would be worth adding the day somebody complains about duplicates; it is not
/// worth building before that.
/// </para>
/// </summary>
public enum DeliveryOutcome
{
    /// <summary>The provider accepted it for delivery.</summary>
    Accepted = 1,

    /// <summary>
    /// The provider refused it and will refuse it again — an invalid address, a blocked domain, a
    /// hard bounce. <b>Definitive</b>, so never retried: the same asymmetry as a payment decline.
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// The send failed in a way that may not fail next time: a connection error, a 5xx, a rate
    /// limit. Retried under backoff until the budget is spent.
    /// </summary>
    TransportFailure = 3
}

/// <summary>
/// An immutable record of one attempt to deliver one notification.
///
/// <para>
/// <b>This type is the second half of Onyx A8</b>, and the half that is easy to miss. The first
/// half is "no customer has ever received any communication from this system" — a missing
/// implementation. The second is "<i>and nothing records that</i>" — a missing table. The first
/// is a day's work to fix; the second is why nobody knew for long enough to write it in an audit.
/// </para>
///
/// <para>
/// So a row is written for <b>every</b> attempt, whatever the outcome, carrying the provider's
/// own message id when there is one, our correlation id, the latency and the error. It is the
/// sixth state-versus-log pairing in this codebase, after <c>Product.Stock</c>,
/// <c>PasswordCredential</c>, <c>Promotion.RedemptionCount</c>, <c>PointsAccount.Balance</c> and
/// <c>Payment.RefundedAmount</c>.
/// </para>
///
/// <para>
/// <b>It deliberately does not hold the body.</b> A verification or reset body contains a
/// plaintext token, and a delivery log is exactly the kind of table that gets copied into a
/// reporting warehouse and read by people who have no business holding credentials. The log
/// records that something was sent and what happened; the notification holds what was sent, and
/// only until it is delivered.
/// </para>
/// </summary>
public sealed class DeliveryAttempt : Entity
{
    public const int MaxDetailLength = 2000;

    public Guid NotificationId { get; private init; }

    /// <summary>1 for the first attempt.</summary>
    public int AttemptNumber { get; private init; }

    public DeliveryOutcome Outcome { get; private init; }

    /// <summary>
    /// Which sender handled it — the logging one in development, a provider in production. Worth
    /// recording because "it was sent" means something very different in each case, and a log
    /// that does not distinguish them is how a development default reaches production unnoticed.
    /// </summary>
    public string Sender { get; private init; } = string.Empty;

    /// <summary>
    /// The provider's own id for the message. Null when there was no answer — never fabricated,
    /// the same rule as <c>ProviderReference</c> in Payments (<b>M4</b>).
    /// </summary>
    public string? ProviderMessageId { get; private init; }

    /// <summary>Ours, so a log line and this row join up. Onyx <b>A16</b>.</summary>
    public string CorrelationId { get; private init; } = string.Empty;

    /// <summary>
    /// The provider's message, or our exception type and message. Truncated — an exception's
    /// <c>ToString()</c> can carry kilobytes of stack trace and a dead-lettering notification
    /// writes several. BUGS-AND-LESSONS §9.
    /// </summary>
    public string? Detail { get; private init; }

    public int LatencyMs { get; private init; }

    public DateTimeOffset OccurredAtUtc { get; private init; }

    private DeliveryAttempt() { }

    internal static DeliveryAttempt Record(
        Guid notificationId,
        int attemptNumber,
        DeliveryOutcome outcome,
        string sender,
        string? providerMessageId,
        string correlationId,
        string? detail,
        int latencyMs,
        DateTimeOffset occurredAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            NotificationId = notificationId,
            AttemptNumber = attemptNumber,
            Outcome = outcome,
            Sender = sender,
            ProviderMessageId = providerMessageId,
            CorrelationId = correlationId,
            Detail = Truncate(detail),
            LatencyMs = latencyMs,
            OccurredAtUtc = occurredAtUtc
        };

    private static string? Truncate(string? detail) =>
        detail is null || detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength];
}
