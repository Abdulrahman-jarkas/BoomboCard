namespace BoomboCard.Notifications.Application.Dispatch;

public sealed class NotificationsOptions
{
    /// <summary>
    /// How often the dispatcher looks for notifications that are due.
    ///
    /// <para>
    /// Five seconds, matching the outbox dispatcher. The pair is not a coincidence: a notification
    /// is queued by an outbox-delivered event, so the end-to-end latency a customer sees is the
    /// sum of two poll intervals, and making one of them slow makes the other one pointless.
    /// </para>
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many notifications one pass will send. Capped so a backlog drains in order.</summary>
    public int BatchSize { get; init; } = 20;

    /// <summary>
    /// How long a claim may sit before the sweep assumes nobody is behind it.
    ///
    /// <para>
    /// Must be comfortably longer than the slowest a send can take — a mail provider timeout plus
    /// the recording transaction. Two minutes against a thirty-second send timeout leaves a wide
    /// margin, and the cost of waiting is a delayed email where the cost of not waiting is two.
    /// </para>
    /// </summary>
    public TimeSpan StaleClaimThreshold { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a delivered notification keeps a rendered body that contained a secret.
    ///
    /// <para>
    /// Short, because the body is a live reset or verification link and the notifications table is
    /// exactly the kind of thing that gets copied into a reporting warehouse. An hour is long
    /// enough to debug a delivery problem while the complaint is fresh, and shorter than the
    /// token's own lifetime so the link is usually already dead.
    /// </para>
    ///
    /// <para>
    /// The token expiring is the primary control; this is defence in depth. Onyx <b>S14</b> stored
    /// reset tokens in plaintext with no expiry and no single-use marker at all.
    /// </para>
    /// </summary>
    public TimeSpan SecretRetention { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often the retention and stale-claim sweeps run.</summary>
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the delivery figures are re-derived and reported.</summary>
    public TimeSpan IntegrityCheckInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a processed outbox message is kept.</summary>
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Where the customer-facing site lives, for verification and reset links.
    ///
    /// <para>
    /// <b>From configuration, and used.</b> Onyx <b>A15</b> bound <c>PaymentGatewayOptions.BaseUrl</c>
    /// from configuration and then hardcoded the URL in the client anyway, so the setting was
    /// decoration — and the same mistake in a reset link means every customer in every environment
    /// gets a link to production.
    /// </para>
    /// </summary>
    public string SiteBaseUrl { get; init; } = string.Empty;
}
