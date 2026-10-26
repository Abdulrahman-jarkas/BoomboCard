using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Sales.Domain.CheckoutSessionAggregate;

public enum CheckoutStatusValue
{
    /// <summary>
    /// Created and priced. Nothing is held: no stock, no promotion use, no points. The customer
    /// is still looking at the basket.
    /// </summary>
    Open = 0,

    /// <summary>
    /// Stock, the promotion use and the points are reserved against this session's id. The
    /// window in which the saga's compensation matters.
    /// </summary>
    Holding = 1,

    /// <summary>An order exists. Terminal for the session.</summary>
    Confirmed = 2,

    /// <summary>
    /// The 20-minute window passed. Whatever was held has been released — or will be, because
    /// the sweeper fires unconditionally. Terminal.
    /// </summary>
    Expired = 3,

    /// <summary>Abandoned by the customer, or by an inline compensation. Terminal.</summary>
    Cancelled = 4
}

/// <summary>
/// The checkout lifecycle, with the legal transitions held in the value object rather than in
/// scattered if-statements.
///
/// <para>
/// <b>Onyx had no session at all</b>, which is why its order creation could not be made safe.
/// <c>POST /api/orders</c> created the order row, saved, then looped the lines deducting stock
/// and saving per line, then computed the total and saved again — so a short line partway
/// through returned HTTP 200 with <c>{ error: "Not enough stock for X" }</c> and left an order
/// with some lines, stock gone for those lines, and a total of zero (<b>D2</b>, <b>D3</b>,
/// <b>M19</b>, <b>A23</b>).
/// </para>
///
/// <para>
/// A session is the durable handle that makes the three-transaction flow recoverable: it commits
/// <b>first</b>, so every hold placed in another module names something that exists, and the
/// expiry sweeper has something to sweep. See <c>CONCEPTS.md</c> §13.
/// </para>
/// </summary>
public sealed class CheckoutStatus : ValueObject
{
    public CheckoutStatusValue Value { get; }

    private CheckoutStatus(CheckoutStatusValue value) => Value = value;

    public static CheckoutStatus Open => new(CheckoutStatusValue.Open);

    public static CheckoutStatus From(CheckoutStatusValue value) => new(value);

    public bool IsOpen => Value is CheckoutStatusValue.Open;

    public bool IsHolding => Value is CheckoutStatusValue.Holding;

    public bool IsConfirmed => Value is CheckoutStatusValue.Confirmed;

    /// <summary>
    /// Whether anything may still be held against this session. The sweeper and the
    /// reconciliation job both ask this rather than enumerating statuses themselves.
    /// </summary>
    public bool IsLive => Value is CheckoutStatusValue.Open or CheckoutStatusValue.Holding;

    public ErrorOr<CheckoutStatus> BeginHolding() =>
        Value switch
        {
            CheckoutStatusValue.Open => new CheckoutStatus(CheckoutStatusValue.Holding),
            CheckoutStatusValue.Holding => CheckoutStatusErrors.AlreadyHolding,
            _ => CheckoutStatusErrors.NotLive
        };

    /// <summary>
    /// Only from <c>Holding</c>. An order cannot exist for a session that never reserved its
    /// stock — which is the invariant <b>M19</b> and <b>D2</b> are about: "an order is created
    /// only from a fully validated, fully priced, fully paid session".
    /// </summary>
    public ErrorOr<CheckoutStatus> Confirm() =>
        Value switch
        {
            CheckoutStatusValue.Holding => new CheckoutStatus(CheckoutStatusValue.Confirmed),
            CheckoutStatusValue.Open => CheckoutStatusErrors.NothingHeld,
            CheckoutStatusValue.Confirmed => CheckoutStatusErrors.AlreadyConfirmed,
            _ => CheckoutStatusErrors.NotLive
        };

    public ErrorOr<CheckoutStatus> Expire() =>
        Value switch
        {
            CheckoutStatusValue.Open or CheckoutStatusValue.Holding =>
                new CheckoutStatus(CheckoutStatusValue.Expired),
            _ => CheckoutStatusErrors.NotLive
        };

    public ErrorOr<CheckoutStatus> Cancel() =>
        Value switch
        {
            CheckoutStatusValue.Open or CheckoutStatusValue.Holding =>
                new CheckoutStatus(CheckoutStatusValue.Cancelled),
            CheckoutStatusValue.Confirmed => CheckoutStatusErrors.AlreadyConfirmed,
            _ => CheckoutStatusErrors.NotLive
        };

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString();
}

public static class CheckoutStatusErrors
{
    public static Error AlreadyHolding =>
        Error.Conflict(
            "Checkout.AlreadyHolding",
            "This checkout has already reserved its stock.");

    public static Error AlreadyConfirmed =>
        Error.Conflict("Checkout.AlreadyConfirmed", "This checkout has already been confirmed.");

    public static Error NothingHeld =>
        Error.Conflict(
            "Checkout.NothingHeld",
            "This checkout has not reserved its stock, so it cannot be confirmed.");

    public static Error NotLive =>
        Error.Conflict(
            "Checkout.NotLive",
            "This checkout has expired or been cancelled. Start a new one.");
}
