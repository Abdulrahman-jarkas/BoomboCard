using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Customers.Domain.Credentials;

/// <summary>
/// A pending password reset: who it is for, the hash of the token that redeems it, when it
/// expires, and whether it has been used.
///
/// <para>
/// Its own aggregate rather than state on <see cref="CustomerAggregate.Customer"/>, for the
/// reason that decides every such question here: a customer may have several outstanding resets
/// (they clicked the link twice), the rows accumulate and are swept, and redeeming one is a
/// write that must not contend with every other write to the customer row. Putting them inside
/// the aggregate would mean loading every reset a customer has ever requested in order to
/// change their name.
/// </para>
///
/// <para>
/// <b>Three properties Onyx's reset had none of</b> (S14): the stored value is a hash, it
/// expires, and it is single-use. The third is the one that needs a mechanism rather than a
/// field — see <see cref="Redeem"/>.
/// </para>
/// </summary>
public sealed class PasswordResetToken : AggregateRoot
{
    /// <summary>
    /// One hour. Long enough to survive a slow mail relay and someone reading their email after
    /// a meeting; short enough that a token sitting in an unattended inbox stops being a
    /// standing key to the account.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public Guid CustomerId { get; private init; }

    /// <summary>Hex SHA-256 of the plaintext. Indexed and unique.</summary>
    public string TokenHash { get; private init; } = string.Empty;

    public DateTimeOffset IssuedAtUtc { get; private init; }

    public DateTimeOffset ExpiresAtUtc { get; private init; }

    public DateTimeOffset? RedeemedAtUtc { get; private set; }

    private PasswordResetToken() { }

    public static PasswordResetToken Issue(
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
    /// Marks the token spent.
    ///
    /// <para>
    /// <b>Single use is enforced by the concurrency token, not by this check.</b> The
    /// <c>IsRedeemed</c> test is a read, so two requests presenting the same token can both pass
    /// it — and a password reset that can be replayed is a password reset an attacker can race.
    /// What actually guarantees it is <c>AggregateRoot.Version</c>: both writers issue
    /// <c>UPDATE ... WHERE Id = @id AND Version = @original</c>, the loser affects zero rows and
    /// raises <c>DbUpdateConcurrencyException</c>, and the retry re-reads the row, finds it
    /// redeemed, and fails properly.
    /// </para>
    ///
    /// <para>
    /// This is the same argument as Catalog's oversell prevention, applied to a different
    /// resource. Onyx's equivalent of both was an in-process <c>SemaphoreSlim</c> (D16) which
    /// protected one instance of one process and leaked permanently on throw.
    /// </para>
    /// </summary>
    public ErrorOr<Success> Redeem(DateTimeOffset nowUtc)
    {
        if (IsRedeemed)
        {
            return PasswordResetErrors.AlreadyRedeemed;
        }

        if (HasExpiredAt(nowUtc))
        {
            return PasswordResetErrors.Expired;
        }

        RedeemedAtUtc = nowUtc;

        return Result.Success;
    }
}

public static class PasswordResetErrors
{
    /// <summary>
    /// One error for "no such token", "already used" and "expired".
    ///
    /// A caller presenting a token either gets the reset or does not; telling them <i>which</i>
    /// way it failed distinguishes a token that never existed from one that did, which is a
    /// guessing oracle on a credential. The reason is logged, not returned.
    /// </summary>
    public static Error Invalid =>
        Error.Validation(
            "PasswordReset.Invalid",
            "That reset link is not valid. Request a new one.");

    public static Error AlreadyRedeemed => Invalid;

    public static Error Expired => Invalid;
}
