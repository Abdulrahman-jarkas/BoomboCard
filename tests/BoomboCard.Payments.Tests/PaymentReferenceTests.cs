using BoomboCard.Payments.Domain.PaymentAggregate;

namespace BoomboCard.Payments.Tests;

/// <summary>
/// The three identifiers, and why there are three.
///
/// <para>
/// <b>Onyx collapsed two of them into one field and lost both</b> —
/// <c>GatewayReference = result?.GatewayReference ?? confirmationCode</c>, which on failure
/// stored a local random six-digit code in the field whose purpose is to identify the transaction
/// at the provider. The register lists the consequences: reconciliation becomes guesswork,
/// refunds are impossible, chargebacks are undefendable.
/// </para>
///
/// <list type="table">
/// <item><term><see cref="PaymentReference"/></term><description>ours, customer-facing. What
/// support looks a payment up by.</description></item>
/// <item><term><see cref="ProviderReference"/></term><description>theirs. What reconciliation
/// matches against a statement, and the only one that can settle a payment.</description></item>
/// <item><term><see cref="IdempotencyKey"/></term><description>ours, sent to them. What makes a
/// retry safe and an unanswered request resolvable.</description></item>
/// </list>
/// </summary>
public class PaymentReferenceTests
{
    // ------------------------------------------------------------------ M5: the idempotency key

    [Fact]
    public void AnIdempotencyKeyIsTheStatedLengthAndBase32()
    {
        var key = IdempotencyKey.New();

        Assert.Equal(IdempotencyKey.Length, key.Value.Length);
        Assert.All(key.Value, character =>
            Assert.True(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(character),
                $"'{character}' is not in the base32 alphabet."));
    }

    /// <summary>
    /// <b>Unguessable, from the OS CSPRNG.</b> 10,000 keys with no collision is not proof of
    /// randomness — it is proof that the generator is not returning a constant or cycling, which
    /// is the failure mode that actually happens when somebody swaps the source for
    /// <c>new Random()</c>.
    /// </summary>
    [Fact]
    public void IdempotencyKeysAreUnique()
    {
        var keys = new HashSet<string>();

        for (var index = 0; index < 10_000; index++)
        {
            Assert.True(keys.Add(IdempotencyKey.New().Value), "an idempotency key repeated.");
        }
    }

    /// <summary>
    /// The alphabet is a power of two and the generator masks the low five bits of a uniformly
    /// random byte, so there is no modulo bias. This asserts the distribution is not obviously
    /// skewed — a non-power-of-two alphabet with the same code would over-represent the first
    /// few characters, which is a real and easy mistake.
    /// </summary>
    [Fact]
    public void TheKeyAlphabetIsUsedWithoutObviousBias()
    {
        var counts = new Dictionary<char, int>();

        for (var index = 0; index < 2_000; index++)
        {
            foreach (var character in IdempotencyKey.New().Value)
            {
                counts[character] = counts.GetValueOrDefault(character) + 1;
            }
        }

        // 2,000 keys x 32 characters = 64,000 draws over 32 symbols, so ~2,000 each. A 3x spread
        // between the most and least common would indicate something badly wrong; true bias from
        // a bad modulo would show as roughly 2x on part of the alphabet.
        Assert.Equal(32, counts.Count);
        Assert.True(
            counts.Values.Max() < counts.Values.Min() * 2,
            $"the alphabet is used unevenly: {counts.Values.Min()}..{counts.Values.Max()}.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TOOSHORT")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA1")]
    public void AMalformedIdempotencyKeyIsRejected(string? value)
    {
        Assert.True(IdempotencyKey.Parse(value).IsError);
    }

    /// <summary>
    /// <c>0</c>, <c>1</c>, <c>8</c> and <c>9</c> are not in the alphabet, so a key is never
    /// ambiguous when read aloud or retyped. Rejected rather than coerced.
    /// </summary>
    [Fact]
    public void CharactersOutsideTheAlphabetAreRejectedRatherThanCoerced()
    {
        Assert.True(IdempotencyKey.Parse(new string('0', IdempotencyKey.Length)).IsError);
        Assert.True(IdempotencyKey.Parse(new string('1', IdempotencyKey.Length)).IsError);
    }

    [Fact]
    public void AnIdempotencyKeyRoundTripsThroughItsText()
    {
        var key = IdempotencyKey.New();
        var parsed = IdempotencyKey.Parse(key.Value);

        Assert.False(parsed.IsError);
        Assert.Equal(key, parsed.Value);
    }

    // ------------------------------------------------------------------ M4: the provider's

    /// <summary>
    /// <b>The only constructor takes a provider's answer, and a blank answer is no answer.</b>
    /// There is deliberately no <c>New()</c>, no default, and no fallback anywhere near this
    /// type — which is what makes <b>M4</b> structural rather than a rule somebody has to
    /// remember.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\t")]
    [InlineData("   ")]
    public void AProviderReferenceCannotBeBuiltWithoutAProviderAnswer(string? value)
    {
        var reference = ProviderReference.FromProvider(value);

        Assert.True(reference.IsError);
        Assert.Equal("ProviderReference.Missing", reference.FirstError.Code);
    }

    [Fact]
    public void AProviderReferenceIsTrimmedAndKeptVerbatimOtherwise()
    {
        var reference = ProviderReference.FromProvider("  ch_3Nk9aB2eZvKYlo2C  ");

        Assert.False(reference.IsError);
        Assert.Equal("ch_3Nk9aB2eZvKYlo2C", reference.Value.Value);
    }

    /// <summary>
    /// Bounded, because it is stored. A provider that returned something absurd would otherwise
    /// fail at the database with a truncation error on the one row that explains where the money
    /// went.
    /// </summary>
    [Fact]
    public void AnOverlongProviderReferenceIsRejected()
    {
        var reference = ProviderReference.FromProvider(
            new string('x', ProviderReference.MaxLength + 1));

        Assert.True(reference.IsError);
        Assert.Equal("ProviderReference.TooLong", reference.FirstError.Code);
    }

    /// <summary>
    /// Two references with the same text are equal, which is what lets a webhook be matched to a
    /// payment by value rather than by string comparison scattered across handlers.
    /// </summary>
    [Fact]
    public void ProviderReferencesCompareByValue()
    {
        Assert.Equal(
            ProviderReference.FromProvider("ch_1").Value,
            ProviderReference.FromProvider("ch_1").Value);

        Assert.NotEqual(
            ProviderReference.FromProvider("ch_1").Value,
            ProviderReference.FromProvider("ch_2").Value);
    }

    // ------------------------------------------------------------------ S13: ours

    [Fact]
    public void APaymentReferenceIsPrefixedAndCryptoRandom()
    {
        var reference = PaymentReference.New();

        Assert.StartsWith("PAY-", reference.Value);
        Assert.Equal(20, reference.Value.Length);
    }

    /// <summary>
    /// <b>S13.</b> Onyx's confirmation code was a six-digit number from <c>new Random()</c>. A
    /// payment reference is shown to whoever holds it, so a guessable one is an enumeration
    /// oracle over other people's transactions.
    /// </summary>
    [Fact]
    public void PaymentReferencesAreUnique()
    {
        var references = new HashSet<string>();

        for (var index = 0; index < 10_000; index++)
        {
            Assert.True(references.Add(PaymentReference.New().Value), "a payment reference repeated.");
        }
    }

    /// <summary>
    /// The alphabet excludes <c>I</c>, <c>O</c>, <c>0</c> and <c>1</c>, because a customer reads
    /// this one down the phone and those four are where mistakes happen.
    /// </summary>
    [Fact]
    public void APaymentReferenceAvoidsAmbiguousCharacters()
    {
        for (var index = 0; index < 200; index++)
        {
            var body = PaymentReference.New().Value["PAY-".Length..];

            Assert.DoesNotContain('I', body);
            Assert.DoesNotContain('O', body);
            Assert.DoesNotContain('0', body);
            Assert.DoesNotContain('1', body);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PAY-")]
    [InlineData("7KQJ2M4XB9WRFTVN")]
    [InlineData("XXX-7KQJ2M4XB9WRFTVN")]
    [InlineData("PAY-7KQJ2M4XB9WRFTV")]
    public void AMalformedPaymentReferenceIsRejected(string? value)
    {
        Assert.True(PaymentReference.Parse(value).IsError);
    }

    [Fact]
    public void APaymentReferenceRoundTripsThroughItsText()
    {
        var reference = PaymentReference.New();
        var parsed = PaymentReference.Parse(reference.Value);

        Assert.False(parsed.IsError);
        Assert.Equal(reference, parsed.Value);
    }

    // ------------------------------------------------------------------ they are not interchangeable

    /// <summary>
    /// <b>The test that states the bug.</b> A payment reference is not a provider reference, and
    /// the type system will not let one stand in for the other.
    ///
    /// <para>
    /// There is no conversion, no shared base type and no implicit operator — so Onyx's
    /// <c>?? confirmationCode</c> cannot be written here even by accident. That is the difference
    /// between a type and a naming convention.
    /// </para>
    /// </summary>
    [Fact]
    public void OursAndTheirsAreDifferentTypesWithNoConversionBetweenThem()
    {
        var ours = PaymentReference.New();
        var theirs = ProviderReference.FromProvider("ch_1").Value;

        Assert.NotEqual(ours.GetType(), theirs.GetType());
        Assert.NotEqual<object>(ours, theirs);

        // And neither type exposes a factory that could manufacture the other's value.
        Assert.Null(typeof(ProviderReference).GetMethod("New"));
        Assert.Null(typeof(PaymentReference).GetMethod("FromProvider"));
    }
}
