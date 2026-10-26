using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Sales.Domain.OrderAggregate;

/// <summary>
/// One line of an order: what was sold, how many, at what price, and how many have shipped.
///
/// <para>
/// <b>Onyx D15</b> is this class done wrong:
/// </para>
///
/// <code>
/// public class OrderLine
/// {
///     public int Id;
///     public int OrderId;
///     public int ProductId;
///     public int Quantity;
///     public decimal UnitPrice;
/// }
/// </code>
///
/// <para>
/// Public <b>fields</b>, not properties — which EF Core ignores entirely by convention, so the
/// type could not be mapped without explicit configuration nobody wrote. No encapsulation, no
/// validation, no identity semantics, and a mutable <c>OrderId</c> that let a line be reassigned
/// to another order by assignment.
/// </para>
///
/// <para>
/// A proper entity inside the <c>Order</c> aggregate: private constructor, private setters, and
/// the price captured at the moment of sale. <see cref="PriceId"/> comes through from the
/// checkout line, so an order can prove which of Catalog's effective-dated prices it was sold
/// at — the end of the chain that closes <b>A6</b>.
/// </para>
/// </summary>
public sealed class OrderItem : Entity
{
    public Guid OrderId { get; private init; }

    public Guid ProductId { get; private init; }

    /// <summary>Copied at the sale, so renaming a product does not rewrite old invoices.</summary>
    public string Sku { get; private init; } = string.Empty;

    public string Name { get; private init; } = string.Empty;

    public int Quantity { get; private init; }

    public Money UnitPrice { get; private init; } = null!;

    public Money LineTotal { get; private init; } = null!;

    /// <summary>Catalog's identity for the price this was sold at.</summary>
    public Guid PriceId { get; private init; }

    /// <summary>
    /// How many have shipped. Supports partial shipment, which <c>ISSUES.md</c> §7 lists as a
    /// missing capability — Onyx had a single <c>Shipped</c> status nothing ever assigned.
    /// </summary>
    public int QuantityShipped { get; private set; }

    public bool IsFullyShipped => QuantityShipped >= Quantity;

    public int QuantityOutstanding => Quantity - QuantityShipped;

    private OrderItem() { }

    internal static OrderItem Create(
        Guid orderId,
        Guid productId,
        string sku,
        string name,
        int quantity,
        Money unitPrice,
        Money lineTotal,
        Guid priceId) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            ProductId = productId,
            Sku = sku,
            Name = name,
            Quantity = quantity,
            UnitPrice = unitPrice,
            LineTotal = lineTotal,
            PriceId = priceId,
            QuantityShipped = 0
        };

    internal ErrorOr<Success> Ship(int quantity)
    {
        if (quantity <= 0)
        {
            return Error.Validation(
                "Order.ShipQuantityMustBePositive",
                $"The quantity shipped for '{Sku}' must be at least one.");
        }

        if (quantity > QuantityOutstanding)
        {
            return Error.Validation(
                "Order.ShipQuantityExceedsOutstanding",
                $"Only {QuantityOutstanding} of '{Sku}' remain to ship.");
        }

        QuantityShipped += quantity;

        return Result.Success;
    }
}
