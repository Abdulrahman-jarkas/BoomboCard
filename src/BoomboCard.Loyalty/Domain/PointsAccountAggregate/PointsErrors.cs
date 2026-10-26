using ErrorOr;

namespace BoomboCard.Loyalty.Domain.PointsAccountAggregate;

public static class PointsErrors
{
    public static Error AccountNotFound(Guid customerId) =>
        Error.NotFound(
            "Points.AccountNotFound",
            $"No points account exists for customer '{customerId}'.");

    public static Error AccountAlreadyExists =>
        Error.Conflict(
            "Points.AccountAlreadyExists",
            "That customer already has a points account.");

    public static Error AccountClosed =>
        Error.Conflict(
            "Points.AccountClosed",
            "That points account is closed and cannot be changed.");

    public static Error PointsMustBePositive =>
        Error.Validation(
            "Points.MustBePositive",
            "A number of points must be greater than zero.");

    /// <summary>
    /// The error Onyx threw <b>after</b> committing the order (<b>D4</b>), as an
    /// <c>InvalidOperationException</c> that surfaced as a 500 while the order and every stock
    /// decrement stood. Here it is returned from the quote, before anything is written.
    /// </summary>
    public static Error InsufficientPoints(int requested, int available) =>
        Error.Validation(
            "Points.Insufficient",
            $"That would redeem {requested} points but only {available} are available.");

    public static Error BelowMinimumRedemption(int minimum) =>
        Error.Validation(
            "Points.BelowMinimumRedemption",
            $"At least {minimum} points must be redeemed at once.");

    public static Error ReferenceRequired =>
        Error.Validation(
            "Points.ReferenceRequired",
            "Every points movement must name what caused it.");

    public static Error ActorRequired =>
        Error.Forbidden(
            "Points.ActorRequired",
            "A manual adjustment must name the administrator making it.");

    public static Error NothingToReverse =>
        Error.Conflict(
            "Points.NothingToReverse",
            "No earning was recorded against that reference, so there is nothing to reverse.");
}
