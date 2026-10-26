using ErrorOr;

namespace BoomboCard.Promotions.Domain.PromotionAggregate;

/// <summary>
/// Every way applying a promotion can fail, each with its own code and its own message.
///
/// <para>
/// <b>These name their reason, unlike the sign-in errors, and the contrast is deliberate.</b>
/// Phase 1 collapsed every authentication failure into one response because distinguishing them
/// turns a login endpoint into an account-enumeration oracle. A promotion code is not a
/// credential: the customer typed it themselves, and "your basket is below the minimum" is the
/// entire value of the response. Being vague here would cost a support ticket and protect
/// nothing.
/// </para>
///
/// <para>
/// Onyx <b>M15</b> is the alternative taken to its conclusion. An unrecognised code returned a
/// discount rate of <c>0m</c> and the order went through as though the customer had used a valid
/// one — so a typo became silently no discount, with no error, no log, and nothing the customer
/// or support could look at.
/// </para>
/// </summary>
public static class PromotionErrors
{
    // ---------------------------------------------------------------- configuration

    public static Error WindowEndsBeforeItStarts =>
        Error.Validation(
            "Promotion.WindowEndsBeforeItStarts",
            "A promotion's end instant must be after its start instant.");

    public static Error DiscountMustBePositive =>
        Error.Validation(
            "Promotion.DiscountMustBePositive",
            "A promotion must take something off. A zero discount is a configuration mistake.");

    public static Error RateAboveOneHundredPercent =>
        Error.Validation(
            "Promotion.RateAboveOneHundredPercent",
            "A percentage discount cannot exceed 100%.");

    public static Error CurrencyMismatch(string discountCurrency, string basketCurrency) =>
        Error.Validation(
            "Promotion.CurrencyMismatch",
            $"This promotion is denominated in {discountCurrency} and does not apply to a "
            + $"{basketCurrency} basket.");

    public static Error CapMustBePositive =>
        Error.Validation(
            "Promotion.CapMustBePositive",
            "A usage cap must be at least one, or absent for unlimited.");

    public static Error ScopeRequiresProducts =>
        Error.Validation(
            "Promotion.ScopeRequiresProducts",
            "A product-scoped promotion must name at least one product.");

    public static Error OnlyDraftsCanBeEdited =>
        Error.Conflict(
            "Promotion.OnlyDraftsCanBeEdited",
            "A promotion can only be reconfigured while it is a draft. Customers may already "
            + "have seen the terms of a live campaign.");

    public static Error CodeAlreadyExists(string code) =>
        Error.Conflict(
            "Promotion.CodeAlreadyExists",
            $"A promotion with code '{code}' already exists.");

    public static Error NotFound(Guid promotionId) =>
        Error.NotFound("Promotion.NotFound", $"Promotion '{promotionId}' was not found.");

    // ---------------------------------------------------------------- applying a code

    /// <summary>
    /// An unknown code and a paused one are the same response on purpose — the difference is
    /// internal campaign state the customer has no way to act on, and a paused campaign is
    /// usually about to be either resumed or closed.
    /// </summary>
    public static Error CodeNotRecognised =>
        Error.NotFound(
            "Promotion.CodeNotRecognised",
            "That promotion code is not recognised.");

    public static Error NotStartedYet(DateTimeOffset startsAtUtc) =>
        Error.Validation(
            "Promotion.NotStartedYet",
            $"That promotion does not start until {startsAtUtc:u}.");

    public static Error Expired =>
        Error.Validation("Promotion.Expired", "That promotion has ended.");

    public static Error MinimumSpendNotMet(decimal minimum, string currencyCode) =>
        Error.Validation(
            "Promotion.MinimumSpendNotMet",
            $"That promotion needs a subtotal of at least {minimum:0.00} {currencyCode}.");

    public static Error UsageCapReached =>
        Error.Conflict(
            "Promotion.UsageCapReached",
            "That promotion has been fully redeemed.");

    public static Error CustomerCapReached(int cap) =>
        Error.Conflict(
            "Promotion.CustomerCapReached",
            cap == 1
                ? "You have already used that promotion."
                : $"That promotion can be used at most {cap} times per customer.");

    public static Error NotApplicableToBasket =>
        Error.Validation(
            "Promotion.NotApplicableToBasket",
            "That promotion does not apply to anything in your basket.");

    public static Error CannotCombine =>
        Error.Conflict(
            "Promotion.CannotCombine",
            "That promotion cannot be combined with another promotion on the same order.");

    public static Error ReferenceRequired =>
        Error.Validation(
            "Promotion.ReferenceRequired",
            "A checkout session reference is required to reserve or release a promotion.");
}
