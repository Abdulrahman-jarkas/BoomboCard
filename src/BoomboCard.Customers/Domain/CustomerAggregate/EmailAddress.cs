using System.Text.RegularExpressions;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Customers.Domain.CustomerAggregate;

/// <summary>
/// An email address, normalised so that uniqueness and lookup are properties of the value
/// rather than of how someone typed it.
///
/// <para>
/// <b>This value object is the fix for a SQL injection.</b> Onyx S1 looked customers up on the
/// unauthenticated login path with
/// <c>$"SELECT * FROM Customers WHERE Email = '{email}' COLLATE NOCASE"</c> — string
/// interpolation into raw SQL, reachable before any authentication, on a table holding every
/// password hash in the system. Its author's note in the PR was that they had "verified it
/// behaves correctly for the accounts I tested with" (X15).
/// </para>
///
/// <para>
/// The interesting part is <i>why</i> the raw SQL was there at all: they wanted
/// case-insensitive matching and reached for <c>COLLATE NOCASE</c> to get it — which also welded
/// the query to SQLite while the same PR claimed the provider was swappable in one line (X13).
/// Normalising to lower case here means the comparison is an ordinary equality predicate on an
/// ordinary indexed column, so there is nothing for raw SQL to do. The vulnerability was a
/// symptom of putting a collation concern where a domain concern belonged.
/// </para>
///
/// <para>
/// Validation is deliberately structural rather than RFC-complete: a single <c>@</c> with
/// something either side, a dot in the domain, no whitespace. An address is only really proven
/// by sending mail to it, which is what <see cref="Credentials.EmailVerificationToken"/> is for.
/// A regex that rejects valid-but-unusual addresses is a worse failure than one that accepts a
/// deliverable-looking address and then fails to deliver.
/// </para>
/// </summary>
public sealed partial class EmailAddress : ValueObject
{
    public const int MaxLength = 256;

    public string Value { get; }

    private EmailAddress(string value) => Value = value;

    public static ErrorOr<EmailAddress> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EmailAddressErrors.Required;
        }

        // Lower-cased with the invariant culture, never the current one. Onyx A44 shipped a
        // ToTitleCase helper over CurrentCulture; under tr-TR, "I".ToLower() is "ı", so a
        // culture-sensitive normalisation would make two spellings of the same address collide
        // or fail to collide depending on the server's locale.
        var normalised = value.Trim().ToLowerInvariant();

        if (normalised.Length > MaxLength)
        {
            return EmailAddressErrors.TooLong;
        }

        if (!AllowedPattern().IsMatch(normalised))
        {
            return EmailAddressErrors.InvalidFormat;
        }

        return new EmailAddress(normalised);
    }

    /// <summary>
    /// The local part, redacted to its first character, plus the full domain:
    /// <c>a***@example.com</c>.
    ///
    /// Onyx A42 logged
    /// <c>$"Fetched customer profile: {customer.Email}, credit limit {customer.CreditLimit:C}"</c>
    /// — an interpolated string, so it also bypassed structured logging entirely, writing both
    /// an identifier and a financial detail into plaintext logs on every profile read. Anything
    /// here that logs an address logs this instead.
    /// </summary>
    public string Redacted =>
        Value.IndexOf('@') is var at && at > 0
            ? $"{Value[0]}***{Value[at..]}"
            : "***";

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPattern();

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    /// Redacted, on purpose. <c>ToString()</c> is what an interpolated log message calls, so the
    /// unsafe thing to do has to be the thing that is hard to do by accident. Code that genuinely
    /// needs the address — addressing an email — asks for <see cref="Value"/> and says so.
    /// </summary>
    public override string ToString() => Redacted;
}

public static class EmailAddressErrors
{
    public static Error Required =>
        Error.Validation("Email.Required", "An email address is required.");

    public static Error TooLong =>
        Error.Validation(
            "Email.TooLong",
            $"An email address may be at most {EmailAddress.MaxLength} characters.");

    public static Error InvalidFormat =>
        Error.Validation("Email.InvalidFormat", "That does not look like an email address.");
}
