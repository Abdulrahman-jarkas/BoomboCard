using BoomboCard.Customers.Domain.Credentials;
using BoomboCard.Customers.Domain.CustomerAggregate;

namespace BoomboCard.Customers.Tests;

/// <summary>
/// The value objects and token aggregates that hold the credential rules.
/// </summary>
public sealed class CredentialTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    // ----------------------------------------------------------------- EmailAddress (Onyx S1)

    [Theory]
    [InlineData("Ada@Example.COM", "ada@example.com")]
    [InlineData("  ada@example.com  ", "ada@example.com")]
    [InlineData("ADA@EXAMPLE.COM", "ada@example.com")]
    public void EmailAddress_NormalisesSoUniquenessIsAPropertyOfTheValue(string input, string expected)
    {
        // This is what removes the reason Onyx reached for raw SQL. It wanted case-insensitive
        // matching and used COLLATE NOCASE to get it (S1), which also welded the query to SQLite
        // while the same PR claimed the provider was swappable (X13). A normalised column needs
        // no collation trick, so there is nothing for string concatenation to do.
        Assert.Equal(expected, EmailAddress.Create(input).Value.Value);
    }

    [Fact]
    public void EmailAddress_TwoSpellingsOfOneAddressAreEqual()
    {
        Assert.Equal(
            EmailAddress.Create("Ada@Example.com").Value,
            EmailAddress.Create("ada@example.com").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("no@domain")]
    [InlineData("two@@at.com")]
    [InlineData("spa ce@example.com")]
    public void EmailAddress_RejectsWhatCannotBeAnAddress(string? input) =>
        Assert.True(EmailAddress.Create(input).IsError);

    [Fact]
    public void EmailAddress_ToStringIsRedacted_SoAnInterpolatedLogCannotLeakIt()
    {
        var email = EmailAddress.Create("ada@example.com").Value;

        // Onyx A42 logged $"Fetched customer profile: {customer.Email}, credit limit ..." — an
        // interpolated string, so it bypassed structured logging AND wrote a full address into
        // plaintext logs on every profile read. Making ToString() the safe form means the unsafe
        // thing is the one that has to be asked for by name.
        Assert.Equal("a***@example.com", email.ToString());
        Assert.Equal("a***@example.com", email.Redacted);
        Assert.Equal("ada@example.com", email.Value);
    }

    // ----------------------------------------------------------------- PersonName

    [Fact]
    public void PersonName_CollapsesWhitespaceAndChangesNothingElse()
    {
        // Onyx A44 shipped a ToTitleCase helper over CurrentCulture, which would have rendered
        // McDonald as Mcdonald. It was never called, which is the only reason it never corrupted
        // anyone's name.
        Assert.Equal("Ada Lovelace", PersonName.Create("  Ada   Lovelace ").Value.Value);
        Assert.Equal("Ada McDonald", PersonName.Create("Ada McDonald").Value.Value);
        Assert.Equal("ada o'brien", PersonName.Create("ada o'brien").Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void PersonName_RequiresSomething(string? input) =>
        Assert.True(PersonName.Create(input).IsError);

    // ----------------------------------------------------------------- SecretToken (Onyx S13/S14)

    [Fact]
    public void SecretToken_IsUnpredictableAndLongEnoughToBeUnguessable()
    {
        var tokens = Enumerable.Range(0, 1_000)
            .Select(_ => SecretToken.Generate().Plaintext)
            .ToList();

        // Onyx's reset token was eight characters from new Random() over A-Z0-9 — about 41 bits,
        // from a clock-seeded non-cryptographic generator (S14). Its payment confirmation code
        // was Rng.Next(100000, 999999), which is twenty (S13).
        Assert.Equal(1_000, tokens.Distinct().Count());
        Assert.All(tokens, token => Assert.True(token.Length >= 40));
    }

    [Fact]
    public void SecretToken_IsUrlSafeAndCaseSensitive()
    {
        var token = SecretToken.Generate().Plaintext;

        // Base64url, so the token survives being pasted into a link with no escaping, and keeps
        // its full entropy. Onyx's upper-case-only alphabet threw away a third of its keyspace.
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void SecretToken_StoresOnlyAHash_AndTheHashIsDeterministic()
    {
        var token = SecretToken.Generate();

        // The plaintext travels to the inbox; the hash is what is stored. Lookup is therefore an
        // indexed equality match in the database rather than a string comparison in application
        // code — so there is no comparison for someone to later "simplify" into a
        // non-constant-time ==, which is what HashHelper.VerifyPassword did (S7).
        Assert.NotEqual(token.Plaintext, token.Hash);
        Assert.Equal(token.Hash, SecretToken.HashOf(token.Plaintext));
        Assert.NotEqual(token.Hash, SecretToken.HashOf(token.Plaintext + "x"));
    }

    [Fact]
    public void SecretToken_ToStringNeverRevealsThePlaintext()
    {
        var token = SecretToken.Generate();

        Assert.DoesNotContain(token.Plaintext, token.ToString());
    }

    // ----------------------------------------------------------------- token lifecycle

    [Fact]
    public void APasswordResetToken_IsSingleUse()
    {
        var token = PasswordResetToken.Issue(Guid.NewGuid(), SecretToken.Generate(), Now);

        Assert.False(token.Redeem(Now).IsError);
        Assert.True(token.IsRedeemed);

        var second = token.Redeem(Now);
        Assert.True(second.IsError);

        // "Not found", "already used" and "expired" all return the same error, so the endpoint
        // cannot be used to probe which tokens exist.
        Assert.Equal("PasswordReset.Invalid", second.FirstError.Code);
    }

    [Fact]
    public void APasswordResetToken_ExpiresAfterAnHour()
    {
        var token = PasswordResetToken.Issue(Guid.NewGuid(), SecretToken.Generate(), Now);

        Assert.Equal(Now + TimeSpan.FromHours(1), token.ExpiresAtUtc);

        // Boundary: expiry is inclusive of the instant itself, so a token is dead AT its expiry
        // rather than one tick later. Onyx M17's promo check was the opposite kind of boundary
        // bug — "valid through the end of the listed day", compared with DateTime.Now > expiry.
        Assert.False(token.HasExpiredAt(token.ExpiresAtUtc.AddTicks(-1)));
        Assert.True(token.HasExpiredAt(token.ExpiresAtUtc));
        Assert.True(token.Redeem(token.ExpiresAtUtc).IsError);
    }

    [Fact]
    public void AnEmailVerificationToken_LivesLongerThanAResetToken()
    {
        var verification = EmailVerificationToken.Issue(Guid.NewGuid(), SecretToken.Generate(), Now);
        var reset = PasswordResetToken.Issue(Guid.NewGuid(), SecretToken.Generate(), Now);

        // The asymmetry is deliberate: a lingering verification token is far less dangerous than
        // a lingering key to the account.
        Assert.True(verification.ExpiresAtUtc > reset.ExpiresAtUtc);
        Assert.Equal(TimeSpan.FromDays(7), EmailVerificationToken.Lifetime);
        Assert.Equal(TimeSpan.FromHours(1), PasswordResetToken.Lifetime);
    }

    // ----------------------------------------------------------------- erased credential

    [Fact]
    public void AnErasedCredential_IsASentinelRatherThanNullOrEmpty()
    {
        var erased = PasswordCredential.Erased(Now);

        // Nullable would mean every comparison site has to remember that a missing verifier is
        // not a match, and one that forgets is an authentication bypass. An unmatchable value is
        // safe wherever it is used, including by code written later that does not know erasure
        // exists.
        Assert.True(erased.IsErased);
        Assert.False(string.IsNullOrEmpty(erased.Verifier));
    }

    [Fact]
    public void ACredentialNeverRevealsItsVerifierInToString()
    {
        var credential = PasswordCredential.Create("the-secret-verifier", Now).Value;

        Assert.DoesNotContain("the-secret-verifier", credential.ToString());
    }
}
