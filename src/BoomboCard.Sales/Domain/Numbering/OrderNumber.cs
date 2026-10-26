using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Sales.Domain.Numbering;

/// <summary>
/// A human-quotable order reference: <c>BC-00000123</c>.
///
/// <para>
/// Onyx <b>D8</b>: <c>private static int _nextOrderNumber = 1000;</c> on the controller,
/// incremented with <c>_nextOrderNumber++</c>. Four problems in one line — it reset to 1000 on
/// every process restart, so numbers repeated; it was per-process, so two instances handed out
/// the same numbers; <c>++</c> on a static <c>int</c> from concurrent requests is not atomic; and
/// the number was never written to the order anyway, only logged. Its
/// <c>ARCHITECTURE.md</c> claimed the API was "stateless at the process level" (<b>X3</b>).
/// </para>
///
/// <para>
/// Format decisions, since this is a string customers read down a phone line: a fixed
/// <c>BC-</c> prefix so a support agent can tell an order number from an invoice number or a
/// session id at a glance; zero-padded to eight digits so it is fixed-width and sorts
/// lexically; and no letters beyond the prefix, so there is no O/0 or I/1 to mishear.
/// </para>
///
/// <para>
/// Deliberately <b>not</b> the order's <c>Guid</c> id. A Guid is unreadable aloud, and exposing
/// it as the public reference means the internal identifier and the customer-facing one are the
/// same thing — so it cannot be changed later without breaking every receipt already issued.
/// </para>
/// </summary>
public sealed class OrderNumber : ValueObject
{
    private const string Prefix = "BC-";
    private const int Digits = 8;

    public string Value { get; }

    private OrderNumber(string value) => Value = value;

    internal static OrderNumber FromSequence(long sequence) =>
        new($"{Prefix}{sequence.ToString().PadLeft(Digits, '0')}");

    public static ErrorOr<OrderNumber> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Error.Validation("OrderNumber.Required", "An order number is required.");
        }

        var normalised = value.Trim().ToUpperInvariant();

        return normalised.StartsWith(Prefix, StringComparison.Ordinal)
            && normalised.Length == Prefix.Length + Digits
            && normalised[Prefix.Length..].All(char.IsAsciiDigit)
            ? new OrderNumber(normalised)
            : Error.Validation(
                "OrderNumber.InvalidFormat",
                $"An order number looks like {Prefix}00000123.");
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}

/// <summary>
/// The single persisted row that hands out order numbers.
///
/// <para>
/// <b>A table row under a concurrency token, not a database sequence.</b> A sequence would be
/// the obvious answer and is provider-specific: <c>HasSequence</c> generates
/// <c>CREATE SEQUENCE</c>, which SQLite does not have — and this module references no provider
/// on purpose. The same reasoning that keeps <c>AggregateRoot.Version</c> a <c>uint</c> bumped by
/// an interceptor rather than a <c>rowversion</c> (CONCEPTS §24).
/// </para>
///
/// <para>
/// <b>Gap-tolerant, which D8's obligation line asks for explicitly.</b> A failed or rolled-back
/// order burns its number and leaves a hole, and that is correct: the alternative is reusing
/// numbers, which means two different orders can be quoted the same reference. Gaps are
/// invisible to customers and fatal to nobody; duplicates are neither.
/// </para>
///
/// <para>
/// <b>And it serialises order creation through one row</b>, which is a real cost stated plainly.
/// Two simultaneous orders contend: one wins, the other gets
/// <c>DbUpdateConcurrencyException</c> and is retried by <c>ResilientCommandDispatcher</c>
/// against the new value. At this system's volume that is free. The scale answer is hi/lo —
/// claim a block of a hundred and hand them out in memory — which removes the contention and
/// widens the gaps, and which is not built because nothing has measured a need for it. A cache
/// goes in when a measurement asks for one.
/// </para>
/// </summary>
public sealed class OrderNumberSequence : AggregateRoot
{
    /// <summary>
    /// A fixed id, because there is exactly one of these rows ever. Hardcoded rather than
    /// discovered, so the allocation is a point read on the primary key rather than a scan.
    /// </summary>
    public static readonly Guid SingletonId =
        Guid.Parse("5a1e5e00-0000-0000-0000-000000000001");

    /// <summary>The next number to hand out. Starts at 1.</summary>
    public long NextValue { get; private set; }

    private OrderNumberSequence() { }

    public static OrderNumberSequence Create() =>
        new() { Id = SingletonId, NextValue = 1 };

    /// <summary>
    /// Takes the next number.
    ///
    /// The version token on this row is what makes it safe: two callers both read the same
    /// <c>NextValue</c>, both increment, and exactly one commits.
    /// </summary>
    public OrderNumber Allocate()
    {
        var number = OrderNumber.FromSequence(NextValue);
        NextValue++;
        return number;
    }
}
