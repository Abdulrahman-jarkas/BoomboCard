namespace BoomboCard.Customers.Application;

/// <summary>
/// The Customers module's settings, bound from the <c>Customers</c> configuration section.
///
/// <para>
/// Onyx A15 is the anti-pattern this avoids: it bound <c>PaymentGatewayOptions</c> from
/// configuration, injected it, and then hardcoded
/// <c>"https://payments.onyx-gateway.test/api/charge"</c> in the client anyway — so the setting
/// existed, was documented, and did nothing, and the gateway could not actually be pointed
/// anywhere. Every value here is read at the point it is used.
/// </para>
/// </summary>
public sealed class CustomersOptions
{
    /// <summary>
    /// HMAC key for <c>ClientFingerprint</c>. <b>Required, with no default</b> — see that class
    /// for why an unkeyed hash of an IP address is not anonymous.
    /// </summary>
    public string ClientFingerprintKey { get; init; } = string.Empty;

    /// <summary>
    /// How long an <c>AuthAttempt</c> row is kept.
    ///
    /// <para>
    /// The rows contain email addresses, including ones typed by people who do not have an
    /// account here, so they cannot be kept indefinitely. Ninety days is long enough to
    /// investigate an incident discovered late and to see a slow campaign, and short enough that
    /// the table is not a growing liability. Enforced by <c>AuthAttemptRetentionJob</c>, because
    /// a retention period nothing deletes against is a policy document, not a control.
    /// </para>
    /// </summary>
    public TimeSpan AuthAttemptRetention { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// How long a processed outbox message is kept after publication.
    ///
    /// <para>
    /// Much shorter than <see cref="AuthAttemptRetention"/>, and for a different reason: this
    /// module's outbox payloads can contain a plaintext verification or reset token (see
    /// <c>ICredentialTokenIssuer</c>), so the row is a secret at rest rather than a record worth
    /// keeping. Seven days leaves enough history to investigate a delivery complaint and is well
    /// past any token's own expiry.
    /// </para>
    /// </summary>
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How often the retention and token-sweep job runs. Spent and expired tokens are swept on
    /// the same pass.
    /// </summary>
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Fails fast on anything missing, at startup, rather than at the first sign-in.
    ///
    /// Onyx read <c>_configuration["Jwt:Key"]!</c> with a null-forgiving operator inside its
    /// login action, so a missing key was a 500 on the first authentication attempt in
    /// production rather than a refusal to start.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientFingerprintKey))
        {
            throw new InvalidOperationException(
                "Customers:ClientFingerprintKey is required and has no default. It keys the HMAC "
                + "over client addresses in the auth log; without a secret key those hashes are "
                + "reversible by enumeration. Set it in User Secrets locally and in the "
                + "environment or key vault when deployed.");
        }

        if (AuthAttemptRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Customers:AuthAttemptRetention must be positive.");
        }

        if (OutboxRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Customers:OutboxRetention must be positive.");
        }

        if (MaintenanceInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Customers:MaintenanceInterval must be positive.");
        }
    }
}
