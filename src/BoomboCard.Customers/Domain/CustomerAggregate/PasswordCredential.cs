using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Customers.Domain.CustomerAggregate;

/// <summary>
/// A stored password verifier, plus the failed-attempt state that throttles guessing against it.
///
/// <para>
/// <b>The domain never sees a password.</b> It stores an opaque verifier string produced by an
/// <c>IPasswordVerifier</c> in the application layer, and compares nothing itself. That keeps
/// the hashing algorithm — a cryptographic and operational concern, with a tuning parameter that
/// has to move as hardware does — out of a layer that must stay free of infrastructure, and it
/// means the plaintext exists only inside one command handler's stack frame.
/// </para>
///
/// <para>
/// Onyx S7 hashed with a single unsalted pass of SHA-256 and compared with <c>==</c>. Unsalted
/// means one rainbow table breaks every account at once and two users with the same password
/// have visibly identical hashes; single-pass SHA-256 is designed to be fast, which is the
/// opposite of the requirement; and ordinary string equality is not constant-time. All three are
/// replaced by delegating to <c>PasswordHasher&lt;T&gt;</c> — see <c>IdentityPasswordVerifier</c>.
/// </para>
///
/// <para>
/// <b>Why the attempt state lives here and the attempt log does not.</b> This is the same split
/// as <c>Product.Stock</c> versus <c>StockMovement</c> in Catalog: the counter is state an
/// invariant is enforced against, so it belongs on the aggregate and is read under the
/// concurrency token; the per-attempt rows are an unbounded audit log, so they are their own
/// table (<see cref="AuthAttempts.AuthAttempt"/>). Deriving "is this account locked" with a
/// <c>COUNT</c> over the log on every sign-in would be both slower and racier.
/// </para>
/// </summary>
public sealed class PasswordCredential : ValueObject
{
    /// <summary>
    /// Failures tolerated before the first lockout. Five is a compromise: high enough that a
    /// person mistyping a password they know is not locked out, low enough that an online
    /// guessing attack gets almost nowhere before the backoff starts biting.
    /// </summary>
    public const int FailuresBeforeLockout = 5;

    private static readonly TimeSpan FirstLockout = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(30);

    /// <summary>Opaque. Its format is the verifier's business, not the domain's.</summary>
    public string Verifier { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Consecutive failures. Reset to zero by a successful sign-in, never decremented.</summary>
    public int FailedAttemptCount { get; }

    public DateTimeOffset? LockedOutUntilUtc { get; }

    private PasswordCredential(
        string verifier,
        DateTimeOffset updatedAtUtc,
        int failedAttemptCount,
        DateTimeOffset? lockedOutUntilUtc)
    {
        Verifier = verifier;
        UpdatedAtUtc = updatedAtUtc;
        FailedAttemptCount = failedAttemptCount;
        LockedOutUntilUtc = lockedOutUntilUtc;
    }

    public static ErrorOr<PasswordCredential> Create(string? verifier, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(verifier))
        {
            return PasswordCredentialErrors.VerifierRequired;
        }

        return new PasswordCredential(verifier, nowUtc, 0, null);
    }

    public bool IsLockedOutAt(DateTimeOffset nowUtc) =>
        LockedOutUntilUtc is { } until && until > nowUtc;

    /// <summary>
    /// Replaces the verifier and clears the lockout.
    ///
    /// Clearing it is deliberate: someone who can prove the old password, or who holds a valid
    /// single-use reset token, has demonstrated control of the account, and continuing to lock
    /// them out would only punish the victim of the guessing attempt.
    /// </summary>
    public ErrorOr<PasswordCredential> Replace(string? verifier, DateTimeOffset nowUtc) =>
        Create(verifier, nowUtc);

    /// <summary>
    /// Counts a failure and, past the threshold, extends the lockout — doubling per failure from
    /// one minute up to a thirty-minute cap.
    ///
    /// <para>
    /// Progressive rather than fixed, and capped rather than unbounded. A fixed window is a
    /// predictable rate an attacker can simply run at; an uncapped doubling turns a modest
    /// guessing attempt into a permanent denial of service against a real customer, which hands
    /// the attacker a different win. The cap and the reset-on-success are what keep this a
    /// throttle rather than a lock.
    /// </para>
    /// </summary>
    public PasswordCredential RecordFailure(DateTimeOffset nowUtc)
    {
        var failures = FailedAttemptCount + 1;

        if (failures < FailuresBeforeLockout)
        {
            return new PasswordCredential(Verifier, UpdatedAtUtc, failures, LockedOutUntilUtc);
        }

        var doublings = failures - FailuresBeforeLockout;
        var window = FirstLockout * Math.Pow(2, Math.Min(doublings, 10));

        if (window > MaxLockout)
        {
            window = MaxLockout;
        }

        return new PasswordCredential(Verifier, UpdatedAtUtc, failures, nowUtc + window);
    }

    public PasswordCredential RecordSuccess() =>
        FailedAttemptCount == 0 && LockedOutUntilUtc is null
            ? this
            : new PasswordCredential(Verifier, UpdatedAtUtc, 0, null);

    /// <summary>
    /// Adopts a re-hashed verifier without touching the attempt state or
    /// <see cref="UpdatedAtUtc"/>.
    ///
    /// Used when the hasher reports that a correct password was verified against an outdated
    /// format — the stored verifier is upgraded transparently on sign-in. It is not a password
    /// <i>change</i>: nothing the customer controls has changed, so claiming it did would put a
    /// misleading date on the account and send them a "your password was changed" notification
    /// they cannot explain.
    /// </summary>
    public PasswordCredential UpgradeVerifier(string verifier) =>
        new(verifier, UpdatedAtUtc, FailedAttemptCount, LockedOutUntilUtc);

    /// <summary>
    /// What an anonymised customer's credential becomes: a verifier no password can satisfy.
    ///
    /// <para>
    /// A sentinel rather than <c>null</c> or an empty string. Nullable would mean every
    /// comparison site has to remember that a missing verifier is not a match, and one that
    /// forgets is an authentication bypass. An unmatchable value is safe wherever it is used,
    /// including by code written later that does not know erasure exists.
    /// </para>
    /// </summary>
    public static PasswordCredential Erased(DateTimeOffset nowUtc) =>
        new(ErasedVerifier, nowUtc, 0, null);

    private const string ErasedVerifier = "erased";

    public bool IsErased => Verifier == ErasedVerifier;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Verifier;
        yield return UpdatedAtUtc;
        yield return FailedAttemptCount;
        yield return LockedOutUntilUtc;
    }

    /// <summary>
    /// Never the verifier. <c>ToString()</c> is what an interpolated log message reaches for, so
    /// the only thing it can reach has to be safe.
    /// </summary>
    public override string ToString() => $"PasswordCredential(failures: {FailedAttemptCount})";
}

public static class PasswordCredentialErrors
{
    public static Error VerifierRequired =>
        Error.Validation("Password.VerifierRequired", "A password verifier is required.");

    public static Error LockedOut =>
        Error.Forbidden(
            "Password.LockedOut",
            "Too many failed sign-in attempts. Try again shortly.");

    public static Error Incorrect =>
        Error.Unauthorized("Password.Incorrect", "The email address or password is incorrect.");
}
