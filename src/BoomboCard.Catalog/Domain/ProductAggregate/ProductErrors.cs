using ErrorOr;

namespace BoomboCard.Catalog.Domain.ProductAggregate;

public static class ProductErrors
{
    public static Error NotFound(Guid productId) =>
        Error.NotFound("Product.NotFound", $"Product '{productId}' was not found.");

    public static Error NotFoundBySku(string sku) =>
        Error.NotFound("Product.NotFoundBySku", $"Product with SKU '{sku}' was not found.");

    public static Error SkuAlreadyExists(string sku) =>
        Error.Conflict("Product.SkuAlreadyExists", $"A product with SKU '{sku}' already exists.");

    public static Error NameRequired =>
        Error.Validation("Product.NameRequired", "A product name is required.");

    public static Error NameTooLong =>
        Error.Validation(
            "Product.NameTooLong",
            $"A product name may be at most {Product.MaxNameLength} characters.");


    public static Error ReferenceRequired =>
        Error.Validation(
            "Product.ReferenceRequired",
            "A stock movement requires a reference — it is both the audit trail and the "
            + "idempotency key.");

    public static Error NotActive(Guid productId) =>
        Error.Conflict(
            "Product.NotActive",
            $"Product '{productId}' is not active and cannot be sold from.");

    public static Error PriceMustBePositive =>
        Error.Validation("Product.PriceMustBePositive", "A price must be greater than zero.");

    public static Error PriceCurrencyMismatch(string countryCode, string expected, string actual) =>
        Error.Validation(
            "Product.PriceCurrencyMismatch",
            $"Country '{countryCode}' trades in {expected}, but the price was given in {actual}.");

    public static Error PriceNotAfterCurrent(string countryCode) =>
        Error.Validation(
            "Product.PriceNotAfterCurrent",
            $"The new price for '{countryCode}' must take effect after the current one.");

    public static Error NoPriceForCountry(string countryCode) =>
        Error.NotFound(
            "Product.NoPriceForCountry",
            $"The product has no open price for country '{countryCode}'.");

    public static Error CannotWithdrawLastPrice =>
        Error.Conflict(
            "Product.CannotWithdrawLastPrice",
            "An active product must keep at least one price. Discontinue it instead.");

    public static Error CannotActivateWithoutPrice =>
        Error.Conflict(
            "Product.CannotActivateWithoutPrice",
            "A product cannot be activated until it has a price in at least one country.");
}
