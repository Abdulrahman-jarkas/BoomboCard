namespace BoomboCard.Api.Authentication;

/// <summary>
/// Token settings, bound from the <c>Jwt</c> configuration section and validated at startup.
///
/// <para>
/// Onyx read <c>_configuration["Jwt:Key"]!</c> inline in its login action with a null-forgiving
/// operator, so a missing key was a 500 on the first authentication attempt in production
/// rather than a refusal to start — and the key it read was committed to
/// <c>appsettings.json</c> alongside a live payment key and a production database password
/// (S5). It then validated neither issuer nor audience on the way back in (S15), so a token
/// signed with the same key by any other system using it would have been accepted.
/// </para>
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// The signing key. <b>Required, no default.</b> User Secrets locally, environment or key
    /// vault when deployed; never in a file that is committed.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    public string Issuer { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    /// <summary>
    /// Eight hours, as Onyx used — the one choice of its auth code worth keeping. Short enough
    /// that a leaked token expires within a working day, long enough not to sign people out
    /// mid-task. A refresh-token flow would let this drop to minutes, and is a real capability
    /// deliberately not in this phase.
    /// </summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(8);

    /// <summary>
    /// 256 bits. HMAC-SHA256 requires a key at least as long as its output, and
    /// <c>SymmetricSecurityKey</c> will happily accept a shorter one and then throw at the first
    /// signing attempt — so the check belongs here, at startup, where it is a refusal to boot
    /// rather than a 500 on the first login.
    /// </summary>
    private const int MinimumKeyBytes = 32;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new InvalidOperationException(
                "Jwt:Key is required and has no default. Set it with "
                + "`dotnet user-secrets set \"Jwt:Key\" \"<a long random string>\"` locally, and "
                + "from the environment or a key vault when deployed. Onyx S5 committed its "
                + "signing key, its payment API key and its production database password to "
                + "appsettings.json.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(Key) < MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"Jwt:Key must be at least {MinimumKeyBytes} bytes for HMAC-SHA256. A shorter "
                + "key is accepted by the key type and then throws when the first token is "
                + "signed.");
        }

        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException(
                "Jwt:Issuer and Jwt:Audience are both required. They are validated on every "
                + "incoming token, which is what stops a token minted by another system sharing "
                + "the key from being accepted here (Onyx S15).");
        }

        if (Lifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Jwt:Lifetime must be positive.");
        }
    }
}
