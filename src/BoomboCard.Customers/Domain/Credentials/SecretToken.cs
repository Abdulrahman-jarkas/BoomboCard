using System.Security.Cryptography;
using BoomboCard.SharedKernel;

namespace BoomboCard.Customers.Domain.Credentials;

/// <summary>
/// A freshly generated single-use secret, split into the part that is sent and the part that is
/// stored.
///
/// <para>
/// <b>Only the hash is stored.</b> A reset token is a bearer credential: whoever holds it can
/// take over the account. Storing it in plaintext means a database read — a backup, a log, an
/// SQL injection like Onyx's own S1 — hands over every pending reset. So the plaintext exists
/// once, travels to the customer's inbox, and is then unrecoverable by anyone including us.
/// </para>
///
/// <para>
/// <b>SHA-256 is the right hash here, and deliberately not what guards a password.</b>
/// <see cref="CustomerAggregate.PasswordCredential"/> needs a slow, salted KDF because a
/// password is low-entropy and human-chosen, so an offline attacker can enumerate the plausible
/// candidates. A 256-bit random token has nothing to enumerate: there is no dictionary of likely
/// values, so iteration count and per-row salt buy nothing, and a fast hash keeps lookup an
/// indexed equality match. Using PBKDF2 here would be cargo-culting the password rule into a
/// problem that is not the password problem.
/// </para>
///
/// <para>
/// Onyx S14: <c>GeneratePasswordResetToken()</c> built an eight-character string from
/// <c>new Random()</c> over <c>A-Z0-9</c> — about 41 bits, from a non-cryptographic generator
/// seeded by the clock, stored in plaintext, with no expiry and no single-use marker. Its
/// confirmation code (S13) was <c>Rng.Next(100000, 999999)</c>, which is twenty bits.
/// </para>
/// </summary>
/// <param name="Plaintext">
/// Sent to the customer and never persisted. The only copy, once this record is discarded.
/// </param>
/// <param name="Hash">Stored, indexed, and compared against on redemption.</param>
public sealed record SecretToken(string Plaintext, string Hash)
{
    /// <summary>
    /// 32 bytes — 256 bits — from the OS CSPRNG, which is past the point where the token's
    /// entropy stops being the weakest thing about the system.
    /// </summary>
    private const int ByteLength = 32;

    /// <summary>
    /// Base64url: URL-safe, so the token survives being pasted into a link without escaping, and
    /// case-sensitive, so it keeps its full entropy. Onyx's upper-case-only alphabet threw away
    /// a third of its already-inadequate keyspace.
    /// </summary>
    public static SecretToken Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(ByteLength);
        var plaintext = Base64Url(bytes);

        return new SecretToken(plaintext, HashOf(plaintext));
    }

    /// <summary>
    /// Hashes a token presented by a caller, so it can be looked up by hash.
    ///
    /// Comparison is therefore a database equality match on a hash rather than a string compare
    /// in application code — no timing side channel to reason about, and no need for a
    /// constant-time comparison that somebody will eventually replace with <c>==</c>.
    /// </summary>
    public static string HashOf(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintext)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Never the plaintext, for the same reason as everywhere else in this module.</summary>
    public override string ToString() => "SecretToken(***)";
}
