using BoomboCard.Customers.Domain.CustomerAggregate;
using ErrorOr;

namespace BoomboCard.Customers.Application.Auth;

/// <summary>
/// What counts as an acceptable password.
///
/// <para>
/// <b>Length only.</b> No required digit, no required symbol, no mixed case. Composition rules
/// measurably push people towards <c>Password1!</c> and towards reusing one password everywhere,
/// and NIST SP 800-63B has recommended against them since 2017 for exactly that reason. Length
/// is the parameter that actually buys entropy, so twelve characters with nothing else forbidden
/// beats eight characters with four character-class rules.
/// </para>
///
/// <para>
/// Onyx had no policy at all: <c>HashPassword</c> accepted any string including the empty one,
/// and nothing in the system ever checked.
/// </para>
///
/// <para>
/// The maximum exists so an unbounded request body cannot be fed to a deliberately expensive
/// KDF — a cheap way to burn server CPU, and the reason a password field needs an upper bound
/// even though long passwords are the goal.
/// </para>
/// </summary>
internal static class PasswordPolicy
{
    public const int MinimumLength = 12;

    public const int MaximumLength = 256;

    public static ErrorOr<Success> Validate(string? password) =>
        password switch
        {
            null or "" => CustomerErrors.PasswordTooShort(MinimumLength),
            { Length: < MinimumLength } => CustomerErrors.PasswordTooShort(MinimumLength),
            { Length: > MaximumLength } => CustomerErrors.PasswordTooLong(MaximumLength),
            _ => Result.Success
        };
}
