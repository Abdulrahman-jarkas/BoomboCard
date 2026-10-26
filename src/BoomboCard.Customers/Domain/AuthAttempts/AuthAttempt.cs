using BoomboCard.Customers.Domain.CustomerAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Customers.Domain.AuthAttempts;

public enum AuthOutcome
{
    Succeeded = 0,

    /// <summary>Wrong password against a real, active account.</summary>
    BadPassword = 1,

    /// <summary>Correct or incorrect — the account was already locked, so nothing was checked.</summary>
    LockedOut = 2,

    /// <summary>Suspended or anonymised.</summary>
    NotActive = 3,

    /// <summary>
    /// No account for that address. Recorded with a null customer id, because the thing worth
    /// knowing is the <i>pattern</i> — a thousand of these from one client is credential
    /// stuffing, and it is invisible if unknown addresses are simply dropped.
    /// </summary>
    NoSuchAccount = 4
}

/// <summary>
/// An immutable record of one sign-in attempt.
///
/// <para>
/// The credential state on <see cref="Customer"/> is the state; these are the log — the same
/// split as <c>Product.Stock</c> and <c>StockMovement</c> in Catalog, and for the same two
/// reasons. The row count grows without bound, so it cannot be a collection on the aggregate.
/// And the lockout is an invariant that has to be enforced under the concurrency token, so the
/// counter has to be state rather than a <c>COUNT</c> recomputed per sign-in.
/// </para>
///
/// <para>
/// Onyx S12: "no rate limiting, no account lockout, no audit of auth attempts". Its login
/// action looked the customer up, compared a hash, and returned <c>Unauthorized()</c> — leaving
/// no trace that anyone had tried. A password-guessing campaign against it would have been
/// invisible both while it ran and afterwards, and the SQL injection on the same endpoint (S1)
/// would have left no evidence either.
/// </para>
///
/// <para>
/// <b>What is deliberately not here.</b> No password, correct or attempted — logging a failed
/// password is how you end up with a plaintext credential store assembled from typos of real
/// ones. And no raw IP address: <see cref="ClientFingerprint"/> is a salted hash, because the
/// operational question is "is this the same client again" and that can be answered without
/// retaining an identifier for everyone who ever failed to sign in.
/// </para>
/// </summary>
public sealed class AuthAttempt : Entity
{
    /// <summary>Null when the address matched no account.</summary>
    public Guid? CustomerId { get; private init; }

    /// <summary>
    /// The address as submitted, normalised. Kept even for <see cref="AuthOutcome.NoSuchAccount"/>
    /// — that is the whole value of the row — but it is personal data, so the retention job
    /// deletes these on a schedule rather than keeping them forever.
    /// </summary>
    public string Email { get; private init; } = string.Empty;

    public AuthOutcome Outcome { get; private init; }

    /// <summary>A salted hash of the client's address, or null when not supplied.</summary>
    public string? ClientFingerprint { get; private init; }

    public DateTimeOffset OccurredAtUtc { get; private init; }

    private AuthAttempt() { }

    internal static AuthAttempt Create(
        Guid? customerId,
        string email,
        AuthOutcome outcome,
        string? clientFingerprint,
        DateTimeOffset occurredAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Email = email,
            Outcome = outcome,
            ClientFingerprint = clientFingerprint,
            OccurredAtUtc = occurredAtUtc
        };

    /// <summary>
    /// For an address with no account. Public because no aggregate exists to produce it — which
    /// is exactly the case Onyx dropped on the floor.
    /// </summary>
    public static AuthAttempt ForUnknownAccount(
        string email,
        string? clientFingerprint,
        DateTimeOffset occurredAtUtc) =>
        Create(null, email, AuthOutcome.NoSuchAccount, clientFingerprint, occurredAtUtc);

    public bool Succeeded => Outcome is AuthOutcome.Succeeded;
}

/// <summary>
/// Turns an outcome into the response the caller gets.
///
/// <para>
/// <b>Every failure returns the same error.</b> Wrong password, no such account, suspended
/// account — all <c>Password.Incorrect</c>, with the same wording. Distinguishing them would
/// turn the login endpoint into an account-enumeration oracle: "no such account" versus
/// "incorrect password" tells an attacker which addresses are registered, which is the first
/// step of every credential-stuffing run. The distinction is recorded in the log, where it is
/// useful to us and not to them.
/// </para>
///
/// <para>
/// Lockout is the one exception, and it is a deliberate trade. Returning a distinct error
/// confirms the account exists. But a customer locked out after mistyping their password needs
/// to be told that waiting will fix it, or they will try again immediately, fail again, extend
/// their own lockout and contact support. The information leak is small — an attacker who has
/// made five attempts against an address already knows it is worth attacking — and the
/// usability cost of hiding it is paid entirely by real customers.
/// </para>
/// </summary>
public static class AuthOutcomeErrors
{
    public static Error For(AuthOutcome outcome) =>
        outcome switch
        {
            AuthOutcome.LockedOut => PasswordCredentialErrors.LockedOut,
            _ => PasswordCredentialErrors.Incorrect
        };
}
