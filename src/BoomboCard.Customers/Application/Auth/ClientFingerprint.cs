using System.Security.Cryptography;
using System.Text;

namespace BoomboCard.Customers.Application.Auth;

/// <summary>
/// Turns a client address into a salted, keyed hash for the auth log.
///
/// <para>
/// <b>Why not just store the IP address.</b> The operational question an auth log answers is
/// "is this the same client again" — a thousand failures from one source is credential stuffing,
/// and the pattern is what matters, not the identity. A hash answers that question exactly as
/// well. Keeping the plaintext address instead means retaining an identifier for everyone who
/// has ever mistyped a password, in a table that by design also records which email addresses
/// they tried.
/// </para>
///
/// <para>
/// <b>Why keyed, and why that key is required configuration.</b> The IPv4 space is small enough
/// to enumerate completely, so an unkeyed hash of an address is not anonymous at all — anyone
/// with the table can recover every address by hashing all four billion candidates in minutes.
/// HMAC with a secret key makes that attack need the key, which is not in the database.
/// </para>
///
/// <para>
/// There is deliberately no default key. <c>Program.cs</c> already fails fast on a missing
/// connection string for the same reason: Onyx S5 shipped a live payment key and a production
/// database password in <c>appsettings.json</c>, and a security control with a convenient
/// built-in default is a security control that is off in production without anyone noticing.
/// </para>
/// </summary>
internal sealed class ClientFingerprint(CustomersOptions options)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.ClientFingerprintKey);

    /// <summary>
    /// Truncated to 16 bytes. Full-width collision resistance is pointless here — the value is
    /// only ever compared with other values from this same function — and a shorter column
    /// indexes better.
    /// </summary>
    public string? Of(string? clientAddress)
    {
        if (string.IsNullOrWhiteSpace(clientAddress))
        {
            return null;
        }

        var hash = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(clientAddress.Trim()));

        return Convert.ToHexString(hash.AsSpan(0, 16));
    }
}
