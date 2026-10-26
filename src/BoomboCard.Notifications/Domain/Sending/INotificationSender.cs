using BoomboCard.Notifications.Domain.NotificationAggregate;

namespace BoomboCard.Notifications.Domain.Sending;

/// <summary>
/// One message, as a sender needs it. Everything already rendered — a sender formats and
/// transmits, and decides nothing.
/// </summary>
/// <param name="NotificationId">Ours, so a provider's logs and ours can be joined.</param>
/// <param name="Kind">What it is about. Senders may use it to pick a provider stream or tag.</param>
/// <param name="Recipient">Where it goes.</param>
/// <param name="Subject">Already rendered.</param>
/// <param name="Body">
/// Already rendered, and <b>may contain a plaintext token</b> — see
/// <paramref name="ContainsSecret"/>.
/// </param>
/// <param name="ContainsSecret">
/// Whether the body holds a credential.
///
/// <para>
/// <b>This flag is on the envelope because a sender has to respect it.</b> The development
/// logging sender writes messages where a developer can read them, which is exactly what makes
/// it useful and exactly what would put live reset links in a log file. It redacts the body when
/// this is set, and the test <c>TheLoggingSenderNeverWritesASecretBody</c> says so.
/// </para>
/// </param>
public sealed record NotificationEnvelope(
    Guid NotificationId,
    NotificationKind Kind,
    NotificationAddress Recipient,
    string Subject,
    string Body,
    bool ContainsSecret);

/// <summary>
/// What a sender did.
///
/// <para>
/// Three outcomes, not four. See <see cref="DeliveryOutcome"/> for why the
/// <c>Indeterminate</c> case that Payments builds real machinery for is deliberately absent here:
/// a duplicate email is annoying, a duplicate charge is theft, and the guarantee should cost what
/// being wrong costs.
/// </para>
/// </summary>
/// <param name="Outcome">Which of the three answers this was.</param>
/// <param name="ProviderMessageId">
/// The provider's id for the message, when it gave one. <b>Never fabricated</b> — the same rule
/// as <c>ProviderReference</c> in Payments (<b>M4</b>). Null is "they did not say", not "we made
/// one up".
/// </param>
/// <param name="Detail">
/// The provider's message, or our exception type and message. Persisted on the attempt row,
/// never shown to a recipient.
/// </param>
/// <param name="LatencyMs">How long it took.</param>
/// <param name="CorrelationId">Ours, per attempt, so a log line and an attempt row join up.</param>
/// <param name="Sender">
/// Which sender handled it. Recorded because "it was sent" means something very different for the
/// logging sender than for a real provider, and a log that does not distinguish them is how a
/// development default reaches production unnoticed.
/// </param>
public sealed record SendResult(
    DeliveryOutcome Outcome,
    string? ProviderMessageId,
    string? Detail,
    int LatencyMs,
    string CorrelationId,
    string Sender)
{
    public bool Accepted => Outcome is DeliveryOutcome.Accepted;

    /// <summary>
    /// Whether the answer settles the matter. A rejection does; a transport failure does not.
    /// </summary>
    public bool IsDefinitive =>
        Outcome is DeliveryOutcome.Accepted or DeliveryOutcome.Rejected;
}

/// <summary>
/// Whatever actually puts a message on the wire.
///
/// <para>
/// <b>An interface here and an implementation in the host</b>, exactly as <c>IPaymentGateway</c>
/// and <c>IAccessTokenIssuer</c> are. The provider, its credential, its base address and its
/// timeout are deployment concerns, and the module that decides <i>what</i> to tell a customer
/// should not also own <i>how</i> to reach a particular vendor. The practical consequence is that
/// <c>BoomboCard.Notifications</c> references no HTTP or SMTP package at all.
/// </para>
///
/// <para>
/// <b>It never throws for a provider outcome, and that is the whole of A16 for this module.</b>
/// Onyx's payment client had <c>catch { }</c> in a retry loop and
/// <c>catch (Exception ex) { Console.WriteLine(...); return new PaymentGatewayResult { Success = false }; }</c>
/// inside the client — all diagnostic information destroyed, with the only trace going to a
/// console no pipeline captured. Here the adapter catches specific exception types, where the type
/// is known, and converts each to the outcome it actually implies. An exception reaching the
/// caller means a bug in the adapter, not a bounced email.
/// </para>
///
/// <para>
/// <b>The roadmap asks for a logging sender in development specifically so the capability is
/// reachable and tested</b> rather than "wired up separately per environment" — which is
/// <b>A32</b>'s lesson. Onyx's CSV import was implemented, tested, advertised in the README, and
/// called by nothing but the test project. A notification path that only works once somebody
/// configures a provider is the same shape: a capability that exists in the code and not in the
/// system.
/// </para>
/// </summary>
public interface INotificationSender
{
    /// <summary>
    /// A name for the attempt row. Stable, short, and distinguishable — <c>logging</c> versus
    /// <c>http:provider</c> — because this is the field that answers "did that message actually
    /// leave the building?".
    /// </summary>
    string Name { get; }

    Task<SendResult> SendAsync(
        NotificationEnvelope envelope,
        CancellationToken cancellationToken = default);
}
