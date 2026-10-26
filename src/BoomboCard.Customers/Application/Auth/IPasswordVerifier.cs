namespace BoomboCard.Customers.Application.Auth;

public enum PasswordVerificationOutcome
{
    Failed = 0,

    Succeeded = 1,

    /// <summary>
    /// The password was correct, but the stored verifier uses an older format or a lower work
    /// factor than the current settings. The caller should re-hash and store the result.
    ///
    /// <para>
    /// This is the mechanism that lets the hashing cost move with hardware without a migration
    /// or a forced password reset: every sign-in transparently upgrades the row it
    /// authenticated. A system without it is pinned forever to whatever work factor it launched
    /// with — and Onyx's was one round of SHA-256 (S7), with no format marker, so it had no way
    /// to tell an old hash from a new one even if someone had wanted to.
    /// </para>
    /// </summary>
    SucceededNeedsRehash = 2
}

/// <summary>
/// Hashes and verifies passwords.
///
/// <para>
/// An abstraction in the application layer, not the domain, because password hashing is a
/// cryptographic and operational concern: it has a tuning parameter that must change over time,
/// and it belongs to the layer allowed to reference infrastructure. The domain stores the opaque
/// result (<c>PasswordCredential.Verifier</c>) and compares nothing.
/// </para>
///
/// <para>
/// It is also the narrowest possible seam. The implementation is seven lines over
/// <c>PasswordHasher&lt;T&gt;</c> — deliberately, because the one thing nobody should be able to
/// do here is write their own password hash. Onyx's <c>HashHelper</c> is what that looks like:
/// <c>Convert.ToHexString(SHA256.HashData(...))</c>, compared with <c>==</c>, unsalted, in a
/// static class in a <c>Shared</c> project (S7).
/// </para>
/// </summary>
internal interface IPasswordVerifier
{
    string Hash(string password);

    PasswordVerificationOutcome Verify(string verifier, string password);
}
