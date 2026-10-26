using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Application.Products.Commands;

internal sealed class DeductStockCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<DeductStockCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        DeductStockCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Lines.Count == 0)
        {
            return Error.Validation("DeductStock.NoLines", "At least one line is required.");
        }

        if (string.IsNullOrWhiteSpace(command.Reference))
        {
            return ProductErrors.ReferenceRequired;
        }

        var nowUtc = timeProvider.GetUtcNow();

        // Collapse duplicate lines for the same product first: the idempotency index is
        // (ProductId, Reference, Reason), so two lines for one product in a single checkout
        // would otherwise collide with themselves.
        var requested = command.Lines
            .GroupBy(line => line.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));

        // Already applied? A retried checkout, or a redelivered message. Return success rather
        // than deducting twice.
        var alreadyApplied = await products.MovementExistsAsync(
            requested.Keys.First(), command.Reference, StockReason.Sale, cancellationToken);

        if (alreadyApplied)
        {
            return Result.Success;
        }

        // One round trip for every line, then one save. Nothing is written until every line
        // has passed, so a short line cannot leave earlier deductions committed (Onyx D2).
        var loaded = await products.GetByIdsAsync(requested.Keys.ToList(), cancellationToken);
        var byId = loaded.ToDictionary(product => product.Id);

        var movements = new List<StockMovement>(requested.Count);

        foreach (var (productId, quantity) in requested)
        {
            if (!byId.TryGetValue(productId, out var product))
            {
                return ProductErrors.NotFound(productId);
            }

            var movement = product.Deduct(quantity, StockReason.Sale, command.Reference, nowUtc);

            if (movement.IsError)
            {
                return movement.Errors;
            }

            movements.Add(movement.Value);
        }

        products.AddMovements(movements);

        // One SaveChanges for every product and every movement. UnitOfWorkBehavior holds the
        // transaction open around this and drains domain events inside it, so the audit rows,
        // the stock changes and anything a handler does commit together or not at all.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}
