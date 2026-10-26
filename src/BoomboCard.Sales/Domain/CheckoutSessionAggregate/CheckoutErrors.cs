using ErrorOr;

namespace BoomboCard.Sales.Domain.CheckoutSessionAggregate;

public static class CheckoutErrors
{
    public static Error NotFound(Guid sessionId) =>
        Error.NotFound("Checkout.NotFound", $"Checkout session '{sessionId}' was not found.");

    public static Error CustomerRequired =>
        Error.Validation("Checkout.CustomerRequired", "A checkout needs a customer.");

    public static Error NoLines =>
        Error.Validation("Checkout.NoLines", "A checkout needs at least one line.");

    public static Error TooManyLines(int maximum) =>
        Error.Validation(
            "Checkout.TooManyLines",
            $"A checkout may contain at most {maximum} distinct products.");

    public static Error Expired =>
        Error.Conflict(
            "Checkout.Expired",
            "This checkout has expired. Start a new one — nothing has been charged.");

    public static Error CannotRepriceWhileHolding =>
        Error.Conflict(
            "Checkout.CannotRepriceWhileHolding",
            "This checkout has already reserved its stock and cannot be re-priced. Cancel it and "
            + "start again.");

    /// <summary>
    /// The error that replaces Onyx's <c>Ok(new { error = "Customer not found" })</c> — HTTP 200
    /// with a failure inside it, so any client checking the status code treated a rejected order
    /// as a completed one (<b>A23</b>).
    /// </summary>
    public static Error CustomerCannotTransact =>
        Error.Forbidden(
            "Checkout.CustomerCannotTransact",
            "This account cannot check out. Verify your email address first.");

    public static Error ProductUnavailable(string sku) =>
        Error.Validation(
            "Checkout.ProductUnavailable",
            $"'{sku}' is not currently available to buy.");

    public static Error ProductNotPricedHere(string sku, string countryCode) =>
        Error.Validation(
            "Checkout.ProductNotPricedHere",
            $"'{sku}' is not sold in {countryCode}.");

    public static Error ProductNotFound(Guid productId) =>
        Error.NotFound("Checkout.ProductNotFound", $"Product '{productId}' was not found.");

    /// <summary>
    /// Insufficient stock, surfaced as a <b>409</b> from the confirm step.
    ///
    /// Onyx returned <c>Ok(new { error = $"Not enough stock for {product.Name}" })</c> — a 200
    /// (<b>A23</b>) — and by then it had already created the order row and deducted stock for
    /// every earlier line (<b>D2</b>).
    /// </summary>
    public static Error InsufficientStock =>
        Error.Conflict(
            "Checkout.InsufficientStock",
            "Something in your basket has just sold out. Nothing has been charged.");

    public static Error TotalDoesNotMatchItsRows =>
        Error.Unexpected(
            "Checkout.TotalDoesNotMatchItsRows",
            "The quote's total does not equal the sum of its lines and adjustments. This is a "
            + "defect; the checkout has been refused rather than charged.");
}
