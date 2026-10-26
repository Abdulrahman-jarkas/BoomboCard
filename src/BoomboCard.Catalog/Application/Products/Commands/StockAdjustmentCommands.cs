using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Application.Products.Commands;

/*
 * Onyx had no way to change stock deliberately. Stock moved in exactly two places: it was
 * decremented by order placement, and it was increased by 50 every 15 minutes by a job that
 * invented it from nowhere (D5). There was no stocktake, no write-off, and no goods-in.
 *
 * These two commands plus ReceiveStock are the only ways stock increases or decreases outside
 * a sale, and every one of them records a movement with a stated reason. That is what makes
 * SUM(Delta) == StockQuantity hold, which is the reconciliation check Onyx never had — and
 * without which its replenishment job silently healed every oversell before anyone could see it.
 */

/// <summary>
/// A stocktake. <paramref name="CountedQuantity"/> is what was physically counted and becomes
/// the recorded figure; the discrepancy is recorded as the movement's delta.
/// </summary>
public sealed record AdjustStockCommand(Guid ProductId, int CountedQuantity, string Reference)
    : IRequest<ErrorOr<StockAdjustmentResult>>, ICatalogRequest;

/// <summary>Returns the discrepancy, because that is the number anyone cares about.</summary>
public sealed record StockAdjustmentResult(int PreviousQuantity, int CountedQuantity, int Delta);

/// <summary>Writes stock off as lost, broken or stolen.</summary>
public sealed record RecordShrinkageCommand(Guid ProductId, int Quantity, string Reference)
    : IRequest<ErrorOr<Success>>, ICatalogRequest;

internal sealed class AdjustStockCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<AdjustStockCommand, ErrorOr<StockAdjustmentResult>>
{
    public async Task<ErrorOr<StockAdjustmentResult>> Handle(
        AdjustStockCommand command,
        CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            return ProductErrors.NotFound(command.ProductId);
        }

        var previousQuantity = product.Stock.Quantity;

        var movement = product.AdjustTo(command.CountedQuantity, command.Reference, timeProvider.GetUtcNow());

        if (movement.IsError)
        {
            return movement.Errors;
        }

        products.AddMovement(movement.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new StockAdjustmentResult(
            previousQuantity,
            command.CountedQuantity,
            command.CountedQuantity - previousQuantity);
    }
}

internal sealed class RecordShrinkageCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<RecordShrinkageCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RecordShrinkageCommand command,
        CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            return ProductErrors.NotFound(command.ProductId);
        }

        var movement = product.RecordShrinkage(command.Quantity, command.Reference, timeProvider.GetUtcNow());

        if (movement.IsError)
        {
            return movement.Errors;
        }

        products.AddMovement(movement.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}
