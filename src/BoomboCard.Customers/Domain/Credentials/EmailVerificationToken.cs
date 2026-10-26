using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Customers.Domain.Credentials;

/// <summary>
/// A pending email verification. Same shape as <see cref="PasswordResetToken"/> — hashed,
/// expiring, single-use, its own aggregate — and separate from it on purpose.
///
/// <para>
/// They could share a table with a discriminator. They do not, because the two tokens are
/// different credentials with different blast radii and different lifetimes: a verification
/// token proves "this inbox received mail", a reset token grants "become this customer". Sharing
/// a table invites sharing a lookup, and a lookup that accepts either kind would let a
/// verification link reset a password. The duplication is thirty lines; the coupling would be a
/// privilege-escalation path.
/// </para>
///
/// <para>
/// Onyx had no email verification at all — <c>ISSUES.md</c> §7 lists it under "capabilities that
/// do not exist". Which also means its <c>_ = Task.Run(() =&gt; SendConfirmationEmail(...))</c>
/// (A8) was firing at addresses nobody had ever confirmed could receive mail, from a method
/// whose body threw.
/// </para>
/// </summary>
public sealed class EmailVerificationToken : AggregateRoot
{
    /// <summary>
    /// Seven days, against the reset token's one hour. The asymmetry is the point: this one is
    /// far less dangerous if it lingers, and the cost of it expiring is a new customer who
    /// cannot check out and has to ask for another link.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public Guid CustomerId { get; private init; }

    public string TokenHash { get; private init; } = string.Empty;

    public DateTimeOffset IssuedAtUtc { get; private init; }

    public DateTimeOffset ExpiresAtUtc { get; private init; }

    public DateTimeOffset? RedeemedAtUtc { get; private set; }

    private EmailVerificationToken() { }

    public static EmailVerificationToken Issue(
        Guid customerId,
        SecretToken token,
        DateTimeOffset nowUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TokenHash = token.Hash,
            IssuedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + Lifetime
        };

    public bool IsRedeemed => RedeemedAtUtc is not null;

    public bool HasExpiredAt(DateTimeOffset nowUtc) => ExpiresAtUtc <= nowUtc;

    /// <summary>
    /// Single use, guaranteed by the version token rather than by the check — see
    /// <see cref="PasswordResetToken.Redeem"/> for the full argument.
    /// </summary>
    public ErrorOr<Success> Redeem(DateTimeOffset nowUtc)
    {
        if (IsRedeemed || HasExpiredAt(nowUtc))
        {
            return EmailVerificationErrors.Invalid;
        }

        RedeemedAtUtc = nowUtc;

        return Result.Success;
    }
}

public static class EmailVerificationErrors
{
    public static Error Invalid =>
        Error.Validation(
            "EmailVerification.Invalid",
            "That verification link is not valid. Request a new one.");
}
