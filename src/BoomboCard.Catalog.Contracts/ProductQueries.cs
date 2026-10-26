using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Contracts;

/// <summary>
/// The price and availability of one product in one country, at one instant.
///
/// <paramref name="PriceId"/> is the point of this contract. A checkout quote stores it, so the
/// system can prove which price the customer was shown rather than asserting it — and a later
/// ProductPriceChanged carrying the superseded id lets the quote decide whether to honour or
/// re-quote.
/// </summary>
public sealed record ProductPricingResponse(
    Guid ProductId,
    string Sku,
    string Name,
    Guid PriceId,
    decimal Amount,
    string CurrencyCode,
    int AvailableQuantity,
    bool IsActive);

/// <summary>
/// Not an <see cref="ICatalogRequest"/>: queries get no transaction. dome-gym's middleware
/// opened one for every request including reads and held it for the whole request lifetime.
/// </summary>
public sealed record GetProductPricingQuery(
    IReadOnlyList<Guid> ProductIds,
    string CountryCode) : IRequest<ErrorOr<List<ProductPricingResponse>>>;
