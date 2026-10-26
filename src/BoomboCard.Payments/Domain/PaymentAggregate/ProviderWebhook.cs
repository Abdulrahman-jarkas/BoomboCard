using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Payments.Domain.PaymentAggregate;

/// <summary>What a provider is telling us happened.</summary>
public enum WebhookKind
{
    PaymentAuthorized = 1,
    PaymentCaptured = 2,
    PaymentDeclined = 3,
    PaymentRefunded = 4,
    PaymentChargedBack = 5
}

/// <summary>
/// One notification received from a payment provider, recorded before it is acted on.
///
/// <para>
/// <b>Why a row and not just an inbox claim.</b> The outbox/inbox pair deduplicates messages this
/// system publishes to itself. A provider webhook arrives from outside, over the public internet,
/// from a sender we do not control — so it needs its own record for three reasons the internal
/// inbox does not cover:
/// </para>
///
/// <list type="bullet">
/// <item><b>Deduplication on the provider's event id</b>, unique at the database. Providers
/// retry aggressively and deliver out of order; a captured notification arriving twice must
/// capture once.</item>
/// <item><b>The raw payload, kept.</b> When a chargeback is disputed six months later, the thing
/// that settles it is what the provider actually sent, not our interpretation of it. Onyx
/// <b>A17</b> kept nothing at all.</item>
/// <item><b>Evidence of what was rejected.</b> A webhook that fails signature verification is
/// still recorded, with its verdict — a sustained run of them is either a misconfiguration or
/// somebody probing the endpoint, and both are worth being able to see.</item>
/// </list>
///
/// <para>
/// A webhook is <b>never trusted on its own</b>. The signature decides whether it is from the
/// provider, and the aggregate's state machine decides whether what it claims is possible. A
/// notification saying "declined" for a payment already captured is refused, because that is not
/// something a provider says — it is either a bug or an attack.
/// </para>
/// </summary>
public sealed class ProviderWebhook : Entity
{
    public const int MaxPayloadLength = 8000;

    /// <summary>
    /// The provider's own id for this notification, and the deduplication key. Unique at the
    /// database.
    /// </summary>
    public string ProviderEventId { get; private init; } = string.Empty;

    public WebhookKind Kind { get; private init; }

    /// <summary>The provider's reference for the transaction the notification is about.</summary>
    public string ProviderReference { get; private init; } = string.Empty;

    /// <summary>
    /// The payment this resolved to, or <c>null</c> when it matched nothing. A webhook for an
    /// unknown reference is kept rather than discarded — it is either a payment this system never
    /// recorded, which is a serious bug, or traffic aimed at the wrong environment.
    /// </summary>
    public Guid? PaymentId { get; private set; }

    public bool SignatureVerified { get; private init; }

    /// <summary>
    /// Whether it was applied to a payment. False for a rejected signature, an unknown
    /// reference, or a claim the state machine refused.
    /// </summary>
    public bool Applied { get; private set; }

    /// <summary>Why it was not applied, when it was not.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>What the provider sent, truncated. The thing that settles a dispute.</summary>
    public string Payload { get; private init; } = string.Empty;

    public DateTimeOffset ReceivedAtUtc { get; private init; }

    private ProviderWebhook() { }

    public static ErrorOr<ProviderWebhook> Receive(
        string? providerEventId,
        WebhookKind kind,
        string? providerReference,
        bool signatureVerified,
        string? payload,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(providerEventId))
        {
            return Error.Validation(
                "Webhook.EventIdRequired",
                "A provider notification must carry its own event id, or it cannot be "
                + "deduplicated — and providers retry.");
        }

        if (string.IsNullOrWhiteSpace(providerReference))
        {
            return Error.Validation(
                "Webhook.ReferenceRequired",
                "A provider notification must name the transaction it is about.");
        }

        return new ProviderWebhook
        {
            Id = Guid.NewGuid(),
            ProviderEventId = providerEventId.Trim(),
            Kind = kind,
            ProviderReference = providerReference.Trim(),
            SignatureVerified = signatureVerified,
            Payload = Truncate(payload),
            ReceivedAtUtc = nowUtc
        };
    }

    public void MarkApplied(Guid paymentId)
    {
        PaymentId = paymentId;
        Applied = true;
        RejectionReason = null;
    }

    public void MarkRejected(string reason, Guid? paymentId = null)
    {
        PaymentId = paymentId;
        Applied = false;
        RejectionReason = reason;
    }

    private static string Truncate(string? payload) =>
        payload is null
            ? string.Empty
            : payload.Length <= MaxPayloadLength
                ? payload
                : payload[..MaxPayloadLength];
}
