namespace BoomboCard.Customers.Contracts;

/// <summary>An issued access token and the instant it stops being valid.</summary>
public sealed record AccessToken(string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Mints an access token for a customer who has already been authenticated.
///
/// <para>
/// <b>Implemented by the host, not by the module</b> — the same seam as
/// <c>AddCatalogModule(Action&lt;DbContextOptionsBuilder&gt;)</c>. The signing key, issuer,
/// audience and lifetime are deployment configuration, and the host is the one place that
/// validates incoming tokens, so it should also be the one place that can produce them. The
/// module therefore references no JWT library at all: it decides <i>whether</i> someone is who
/// they claim to be, and the host decides what a token proving that looks like.
/// </para>
///
/// <para>
/// Onyx built the token inline in its login action from <c>_configuration["Jwt:Key"]!</c> — a
/// null-forgiving read of a key committed to <c>appsettings.json</c> (S5) — and validated
/// neither issuer nor audience on the way back in (S15).
/// </para>
/// </summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(
        Guid customerId,
        string email,
        CustomerRole role,
        bool emailVerified,
        DateTimeOffset nowUtc);
}
