using Microsoft.AspNetCore.Identity;

namespace BoomboCard.Customers.Application.Auth;

/// <summary>
/// <see cref="IPasswordVerifier"/> over ASP.NET Core Identity's <c>PasswordHasher&lt;T&gt;</c>.
///
/// <para>
/// <b>Identity's hasher, without Identity.</b> This takes a dependency on
/// <c>Microsoft.Extensions.Identity.Core</c> for one class and nothing else: no
/// <c>IdentityDbContext</c>, no <c>IdentityUser</c>, no <c>UserManager</c>, no stores, no
/// migrations full of tables this system does not use. The <c>Customer</c>
/// aggregate stays ours — with its own lifecycle, its own invariants, and its own events — and
/// only the part that should never be hand-written is borrowed.
/// </para>
///
/// <para>
/// What that one class gives, which <c>HashHelper</c> did not (S7): PBKDF2-HMAC-SHA512 at
/// 100,000 iterations by default, a 128-bit cryptographically random salt per password, a format
/// marker byte so the verifier is self-describing and upgradeable, and
/// <c>CryptographicOperations.FixedTimeEquals</c> for the comparison. Onyx had none of the five
/// — its hash was one unsalted SHA-256 pass compared with <c>==</c>.
/// </para>
/// </summary>
internal sealed class IdentityPasswordVerifier(IPasswordHasher<PasswordOwner> hasher)
    : IPasswordVerifier
{
    /// <summary>
    /// <c>PasswordHasher&lt;T&gt;</c>'s V3 format does not incorporate the user at all — the
    /// salt is per-hash and random — so the instance is a formality the API requires. A shared
    /// dummy makes that explicit, and avoids a <c>null!</c> at every call site that a later
    /// reader would have to decide was safe.
    /// </summary>
    private static readonly PasswordOwner Owner = new();

    public string Hash(string password) => hasher.HashPassword(Owner, password);

    public PasswordVerificationOutcome Verify(string verifier, string password) =>
        hasher.VerifyHashedPassword(Owner, verifier, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerificationOutcome.Succeeded,
            PasswordVerificationResult.SuccessRehashNeeded =>
                PasswordVerificationOutcome.SucceededNeedsRehash,
            _ => PasswordVerificationOutcome.Failed
        };
}

/// <summary>
/// The type parameter <c>PasswordHasher&lt;TUser&gt;</c> demands and never reads. Internal, so
/// nothing outside this module can mistake it for a user model.
/// </summary>
internal sealed class PasswordOwner;
