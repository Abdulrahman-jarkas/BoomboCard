using BoomboCard.Catalog.Contracts.IntegrationEvents;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate.Events;
using BoomboCard.SharedKernel.Persistence.Outbox;
using MediatR;

namespace BoomboCard.Catalog.Application.Products.IntegrationEvents;

/*
 * The bridge from internal domain events to published contracts — RiverBooks'
 * UserAddressIntegrationEventDispatcherHandler pattern.
 *
 * Each translator does exactly two things: flatten the domain event into a Contracts record,
 * and enqueue it. It performs NO I/O and calls NO other context, because it runs inside the
 * command's transaction. The outbox row commits with the aggregate change, and a background
 * dispatcher publishes it afterwards.
 *
 * Compare RiverBooks' SendConfirmationEmailOrderCreatedEventHandler, which runs inside
 * SaveChangesAsync and from there queries another module's SQL Server database and writes to
 * MongoDB — all on the request's critical path, and all outside any transaction.
 */

internal sealed class OnProductPriceChangedEnqueueIntegrationEvent(IOutboxWriter<CatalogDbContext> outbox)
    : INotificationHandler<ProductPriceChangedEvent>
{
    public Task Handle(ProductPriceChangedEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new ProductPriceChangedIntegrationEvent(
            Guid.NewGuid(),
            notification.ProductId,
            notification.CountryCode,
            notification.PreviousPriceId,
            notification.PreviousAmount,
            notification.NewPriceId,
            notification.NewAmount,
            notification.CurrencyCode,
            notification.EffectiveFrom,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}

internal sealed class OnProductDiscontinuedEnqueueIntegrationEvent(
    IOutboxWriter<CatalogDbContext> outbox,
    Data.Repositories.IProductRepository products)
    : INotificationHandler<ProductDiscontinuedEvent>
{
    public async Task Handle(ProductDiscontinuedEvent notification, CancellationToken cancellationToken)
    {
        // Reading inside a domain event handler is fine — this is the module's own database,
        // inside the same transaction, and the aggregate has already been saved so the row is
        // there to read. Calling ANOTHER context would not be.
        var product = await products.GetByIdAsync(notification.ProductId, cancellationToken);

        if (product is null)
        {
            return;
        }

        outbox.Enqueue(new ProductDiscontinuedIntegrationEvent(
            Guid.NewGuid(),
            notification.ProductId,
            product.Sku.Value,
            notification.OccurredAtUtc));
    }
}

/// <summary>
/// Onyx's StockReconciliationJob is replaced by this one handler: the fact is recorded, a
/// message goes out, and a human or a purchasing flow decides what to do. Nothing invents
/// stock.
/// </summary>
internal sealed class OnStockFellBelowThresholdEnqueueIntegrationEvent(IOutboxWriter<CatalogDbContext> outbox)
    : INotificationHandler<StockFellBelowThresholdEvent>
{
    public Task Handle(StockFellBelowThresholdEvent notification, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new StockFellBelowThresholdIntegrationEvent(
            Guid.NewGuid(),
            notification.ProductId,
            notification.Sku,
            notification.QuantityAfter,
            notification.Threshold,
            notification.OccurredAtUtc));

        return Task.CompletedTask;
    }
}
