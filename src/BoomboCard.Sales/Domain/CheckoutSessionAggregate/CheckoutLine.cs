using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Sales.Domain.CheckoutSessionAggregate;

/// <summary>
/// One merchandise line of a quote, with the price that was quoted captured on it.
///
/// <para>
/// <b><see cref="PriceId"/> is why this record exists rather than just a product id and a
/// quantity.</b> Catalog's <c>ProductPrice</c> has identity and is superseded rather than
/// overwritten, so capturing the id turns "the price the customer was shown" from an assertion
/// into something the system can <i>prove</i> — and it lets a later
/// <c>ProductPriceChangedIntegrationEvent</c>, which carries the superseded id, tell an open
/// session whether its quote is still the one that was advertised.
/// </para>
///
/// <para>
/// Onyx <b>A6</b> is the finding: it served browsing from a never-invalidated cache and ordering
/// from the live row, so after any price change a shopper saw one price and was charged another,
/// with nothing recording which was which.
/// </para>
///
/// <para>
/// <see cref="Sku"/> and <see cref="Name"/> are copied too, deliberately. They are what goes on
/// the order and the receipt, and a product renamed six months later must not retroactively
/// change what a historical invoice says was sold.
/// </para>
/// </summary>
public sealed class CheckoutLine : Entity
{
    public Guid CheckoutSessionId { get; private init; }

    public Guid ProductId { get; private init; }

    public string Sku { get; private init; } = string.Empty;

    public string Name { get; private init; } = string.Empty;

    public int Quantity { get; private init; }

    /// <summary>The unit price quoted, in the session's currency.</summary>
    public Money UnitPrice { get; private init; } = null!;

    /// <summary>
    /// Catalog's identity for the price that produced <see cref="UnitPrice"/>. The proof.
    /// </summary>
    public Guid PriceId { get; private init; }

    /// <summary>
    /// <c>UnitPrice × Quantity</c>, <b>rounded once, here</b> — step 1 of the pipeline.
    ///
    /// Stored rather than recomputed, because this is the figure the customer was shown for the
    /// line and the figure the subtotal is the sum of. Recomputing it elsewhere would be a
    /// second place that has to round the same way.
    /// </summary>
    public Money LineTotal { get; private init; } = null!;

    private CheckoutLine() { }

    internal static ErrorOr<CheckoutLine> Create(
        Guid sessionId,
        Guid productId,
        string sku,
        string name,
        int quantity,
        Money unitPrice,
        Guid priceId)
    {
        if (quantity <= 0)
        {
            return Error.Validation(
                "Checkout.QuantityMustBePositive",
                $"The quantity for '{sku}' must be at least one.");
        }

        if (quantity > MaxQuantityPerLine)
        {
            return Error.Validation(
                "Checkout.QuantityTooLarge",
                $"At most {MaxQuantityPerLine} of one product may be ordered at a time.");
        }

        return new CheckoutLine
        {
            Id = Guid.NewGuid(),
            CheckoutSessionId = sessionId,
            ProductId = productId,
            Sku = sku,
            Name = name,
            Quantity = quantity,
            UnitPrice = unitPrice,
            PriceId = priceId,
            // Money.MultiplyBy rounds, so this is the single rounding of the line.
            LineTotal = unitPrice.MultiplyBy(quantity)
        };
    }

    /// <summary>
    /// A sanity bound rather than a business rule. Onyx <b>A28</b> left its page size unbounded
    /// and <c>?take=1000000</c> was a one-request denial of service; an unbounded quantity is
    /// the same shape against the stock path.
    /// </summary>
    public const int MaxQuantityPerLine = 1_000;
}
