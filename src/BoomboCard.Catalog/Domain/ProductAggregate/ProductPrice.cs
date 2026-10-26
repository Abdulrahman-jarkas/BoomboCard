using BoomboCard.SharedKernel;

namespace BoomboCard.Catalog.Domain.ProductAggregate;

/// <summary>
/// One country's price for a product, over a half-open validity window
/// <c>[EffectiveFrom, EffectiveTo)</c>.
///
/// An entity rather than a value object because it has identity that outlives its values: a
/// checkout quote stores the ProductPriceId it was built from, which is what lets the system
/// PROVE what a customer was shown rather than merely assert it.
///
/// Onyx A6 is the bug this closes. Browsing read a never-invalidated in-memory cache while
/// ordering read the live row, so after any price change a shopper was shown one price and
/// charged another — and with no confirmation email (A8) nobody ever found out. It also had no
/// price history whatsoever, so "what did this cost in March" was unanswerable.
///
/// Superseding rather than overwriting is deliberate: an old price is a historical fact that
/// invoices and disputes still refer to.
/// </summary>
public sealed class ProductPrice : Entity
{
    public Guid ProductId { get; private init; }

    public string CountryCode { get; private init; } = string.Empty;

    public Money Amount { get; private init; } = null!;

    public DateTimeOffset EffectiveFrom { get; private init; }

    public DateTimeOffset? EffectiveTo { get; private set; }

    private ProductPrice() { }

    internal static ProductPrice Create(
        Guid productId,
        Country country,
        Money amount,
        DateTimeOffset effectiveFrom) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            CountryCode = country.Code,
            Amount = amount,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = null
        };

    public bool IsEffectiveAt(DateTimeOffset at) =>
        EffectiveFrom <= at && (EffectiveTo is null || EffectiveTo > at);

    public bool IsCurrentlyOpen => EffectiveTo is null;

    internal void SupersedeAt(DateTimeOffset at) => EffectiveTo = at;
}
