using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Contracts.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace BoomboCard.Api.Authentication;

/// <summary>
/// The host's implementation of <see cref="IAccessTokenIssuer"/>.
///
/// <para>
/// It lives here, not in the Customers module, because the signing key and the token format are
/// deployment concerns and because this is the process that validates incoming tokens — the one
/// place that checks a signature should be the one place that produces one. The module decides
/// <i>whether</i> someone is who they claim to be; this decides what a token saying so looks
/// like. The module references no JWT package at all as a result.
/// </para>
/// </summary>
public sealed class JwtAccessTokenIssuer(JwtOptions options) : IAccessTokenIssuer
{
    private readonly SigningCredentials _credentials = new(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)),
        SecurityAlgorithms.HmacSha256);

    private static readonly JwtSecurityTokenHandler Handler = new();

    public AccessToken Issue(
        Guid customerId,
        string email,
        CustomerRole role,
        bool emailVerified,
        DateTimeOffset nowUtc)
    {
        var expiresAt = nowUtc + options.Lifetime;

        List<Claim> claims =
        [
            // Both the standard subject claim and our own, because ASP.NET Core's
            // ClaimsIdentity maps NameIdentifier for anything that expects it while the
            // authorization handlers read the explicit one. Named in one place
            // (BoomboCardClaims) so the token and the policy that reads it cannot drift.
            new(JwtRegisteredClaimNames.Sub, customerId.ToString()),
            new(ClaimTypes.NameIdentifier, customerId.ToString()),
            new(BoomboCardClaims.CustomerId, customerId.ToString()),
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Role, role.ToString()),
            new(BoomboCardClaims.EmailVerified, emailVerified ? "true" : "false")
        ];

        // Times come from the injected TimeProvider via nowUtc, never from DateTime.UtcNow.
        // Onyx wrote DateTime.UtcNow.AddHours(8) here — which happened to be correct, unlike
        // the DateTime.Now it wrote into a field called OrderDateUtc elsewhere (A29) — but it
        // also meant no test could control token expiry.
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: nowUtc.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: _credentials);

        return new AccessToken(Handler.WriteToken(token), expiresAt);
    }
}

/// <summary>
/// The one registered claim name used here. <c>JwtRegisteredClaimNames.Sub</c> rather than the
/// long <c>http://schemas…/nameidentifier</c> URI, so the token stays compact.
/// </summary>
internal static class JwtRegisteredClaimNames
{
    public const string Sub = "sub";
}
