using System.Security.Cryptography;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Payments.Domain.PaymentAggregate;

/// <summary>
/// The stable key sent to the provider on <b>every</b> attempt for one payment, so a retry can
/// never become a second charge.
///
/// <para>
/// <b>This type is Onyx M5.</b> Its payment path made three attempts with no idempotency key at
/// all, and the dangerous case is not the obvious one: a timeout where the charge <i>succeeded</i>
/// provider-side but the response was lost. The system reads that as a failure and charges again.
/// </para>
///
/// <para>
/// The pull request asserted the retry was safe because <i>"the gateway call is idempotent on
/// their end, so the retry loop won't double-charge even under a flaky connection"</i>. Nothing
/// in the request identified the payment, so there was no basis for the claim: a provider cannot
/// deduplicate requests it has no way to recognise as the same request. Idempotency is a property
/// of the <b>key</b>, not of the endpoint.
/// </para>
///
/// <para>
/// <b>Generated once, at intent creation, and never regenerated.</b> That is the whole mechanism
/// — a key that changes between attempts is not an idempotency key, it is a correlation id. It is
/// also why the intent commits before any provider is contacted: the key has to be durable before
/// the first attempt, or a crash mid-attempt loses the only thing that makes the retry safe.
/// </para>
///
/// <para>
/// 256 bits from the OS CSPRNG, base32 for a value that may appear in a URL or a support ticket.
/// Not a <c>Guid</c>: <c>Guid.NewGuid()</c> is v4 and fine in practice, but stating the source
/// explicitly is the same discipline <c>SecretToken</c> applies in Customers, and it means nobody
/// has to go and check which Guid version this runtime produces.
/// </para>
/// </summary>
public sealed class IdempotencyKey : ValueObject
{
    /// <summary>32 base32 characters carrying 160 bits — comfortably unguessable, still readable.</summary>
    public const int Length = 32;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string Value { get; }

    private IdempotencyKey(string value) => Value = value;

    /// <summary>A fresh key. Called exactly once per payment, at intent creation.</summary>
    public static IdempotencyKey New()
    {
        var bytes = RandomNumberGenerator.GetBytes(Length);
        var characters = new char[Length];

        for (var index = 0; index < Length; index++)
        {
            // Masking to the low 5 bits of a uniformly random byte: 32 is a power of two, so
            // there is no modulo bias to correct for. A non-power-of-two alphabet would need
            // RandomNumberGenerator.GetInt32 instead.
            characters[index] = Alphabet[bytes[index] & 0b1_1111];
        }

        return new IdempotencyKey(new string(characters));
    }

    /// <summary>Rehydrates a stored key. Used by EF and by nothing else.</summary>
    public static ErrorOr<IdempotencyKey> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != Length)
            return Error.Validation("IdempotencyKey.Malformed", $"An idempotency key is {Length} characters.");

        return value.All(Alphabet.Contains)
            ? new IdempotencyKey(value)
            : Error.Validation("IdempotencyKey.Malformed", "An idempotency key is base32.");
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}

/// <summary>
/// The provider's own identifier for the transaction.
///
/// <para>
/// <b>Onyx M4 is a single line of code</b>, and it is the most consequential one in that
/// codebase:
/// </para>
///
/// <code>
/// GatewayReference = result?.GatewayReference ?? confirmationCode
/// </code>
///
/// <para>
/// On failure it stored the local random six-digit confirmation code in the field whose entire
/// purpose is to identify the transaction <i>at the provider</i>. The register lists the
/// consequences, and they are all of the expensive kind: reconciliation against provider
/// statements becomes guesswork, refunds are impossible because there is nothing to refund
/// against, and chargebacks are undefendable.
/// </para>
///
/// <para>
/// <b>So this type cannot be constructed from anything but a provider response.</b> It is
/// nullable on the payment, it has no fallback and no default, and the aggregate refuses to enter
/// any settled status without one — see <c>Payment.Authorize</c>. There is deliberately no
/// <c>??</c> anywhere near it.
/// </para>
///
/// <para>
/// The customer-facing confirmation reference is <see cref="PaymentReference"/>, a separate value
/// with a separate purpose. Conflating the two is precisely the bug.
/// </para>
/// </summary>
public sealed class ProviderReference : ValueObject
{
    public const int MaxLength = 200;

    public string Value { get; }

    private ProviderReference(string value) => Value = value;

    /// <summary>
    /// The only constructor, and it takes what the provider said. A blank response is a missing
    /// answer, not an empty one, so it is rejected rather than stored.
    /// </summary>
    public static ErrorOr<ProviderReference> FromProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Error.Validation(
                "ProviderReference.Missing",
                "A provider reference must come from a provider response. It is never generated "
                + "locally and never substituted on failure.");
        }

        var trimmed = value.Trim();

        return trimmed.Length > MaxLength
            ? Error.Validation(
                "ProviderReference.TooLong",
                $"A provider reference is at most {MaxLength} characters.")
            : new ProviderReference(trimmed);
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}

/// <summary>
/// Our own customer-facing reference for a payment: <c>PAY-7KQJ2M4XB9WRFTVN</c>.
///
/// <para>
/// What a customer quotes on the phone, and what appears on a receipt. Crypto-random rather than
/// sequential, because a payment reference is shown to whoever holds it and a guessable one is an
/// enumeration oracle over other people's transactions — <b>S13</b>, where Onyx's confirmation
/// code was a six-digit number from <c>new Random()</c>.
/// </para>
///
/// <para>
/// Deliberately <i>not</i> the order number, and deliberately not the provider reference. Three
/// identifiers for three audiences: the order number is what the customer tracks, this is what
/// support looks a payment up by, and the provider reference is what reconciliation matches
/// against a statement. Onyx collapsed the second and third into one field and lost both.
/// </para>
/// </summary>
public sealed class PaymentReference : ValueObject
{
    private const string Prefix = "PAY-";
    private const int RandomLength = 16;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public string Value { get; }

    private PaymentReference(string value) => Value = value;

    public static PaymentReference New()
    {
        var bytes = RandomNumberGenerator.GetBytes(RandomLength);
        var characters = new char[RandomLength];

        for (var index = 0; index < RandomLength; index++)
        {
            characters[index] = Alphabet[bytes[index] & 0b1_1111];
        }

        return new PaymentReference(Prefix + new string(characters));
    }

    public static ErrorOr<PaymentReference> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length != Prefix.Length + RandomLength)
        {
            return Error.Validation(
                "PaymentReference.Malformed",
                $"A payment reference is '{Prefix}' followed by {RandomLength} characters.");
        }

        return value[Prefix.Length..].All(Alphabet.Contains)
            ? new PaymentReference(value)
            : Error.Validation("PaymentReference.Malformed", "A payment reference is base32.");
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}
