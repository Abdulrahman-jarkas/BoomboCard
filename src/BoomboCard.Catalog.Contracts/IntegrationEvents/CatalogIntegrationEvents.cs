using BoomboCard.SharedKernel;

namespace BoomboCard.Catalog.Contracts.IntegrationEvents;

/*
 * Catalog's cross-boundary facts. Public, flattened to primitives, serialisable — because they
 * travel through the outbox, which stores them as JSON.
 *
 * These are translated from internal domain events by handlers in BoomboCard.Catalog
 * (see Application/Products/IntegrationEvents). That translation step is RiverBooks'
 * UserAddressIntegrationEventDispatcherHandler pattern, and it is what keeps the internal
 * domain events free to change without breaking a published contract.
 */

/// <summary>
/// Carries the superseded price id alongside the new one, so a consumer holding a quote built
/// on the old price can decide: honour it, or re-quote. Talabat's equivalent handler cancels
/// every active session touching the product, which ejects customers mid-payment.
/// </summary>
public sealed record ProductPriceChangedIntegrationEvent(
    Guid EventId,
    Guid ProductId,
    string CountryCode,
    Guid? PreviousPriceId,
    decimal? PreviousAmount,
    Guid NewPriceId,
    decimal NewAmount,
    string CurrencyCode,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// A product has been withdrawn from sale. Consumers holding open quotes for it must cancel —
/// this is the one case where cancelling is right, because unlike a price change there is no
/// price left to honour.
/// </summary>
public sealed record ProductDiscontinuedIntegrationEvent(
    Guid EventId,
    Guid ProductId,
    string Sku,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// Replaces Onyx's StockReconciliationJob (D5), which added 50 units from nowhere every 15
/// minutes. Stock is never invented; a human or a purchasing flow is told instead.
/// </summary>
public sealed record StockFellBelowThresholdIntegrationEvent(
    Guid EventId,
    Guid ProductId,
    string Sku,
    int AvailableQuantity,
    int Threshold,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
