using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Application.Products.Commands;

public sealed record ActivateProductCommand(Guid ProductId)
    : IRequest<ErrorOr<Success>>, ICatalogRequest;

/// <summary>
/// Withdraws a product from sale. Onyx could not do this by any means: no delete, no
/// discontinued flag, so a recalled or out-of-season product stayed purchasable forever and
/// the replenishment job kept restocking it (A31, D5).
/// </summary>
public sealed record DiscontinueProductCommand(Guid ProductId)
    : IRequest<ErrorOr<Success>>, ICatalogRequest;

/// <summary>
/// Goods-in against a real delivery. <c>PurchaseOrderReference</c> is both the audit trail and
/// the idempotency key, so the same receipt cannot be recorded twice.
/// </summary>
public sealed record ReceiveStockCommand(Guid ProductId, int Quantity, string PurchaseOrderReference)
    : IRequest<ErrorOr<Success>>, ICatalogRequest;

internal sealed class ActivateProductCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<ActivateProductCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ActivateProductCommand command,
        CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            return ProductErrors.NotFound(command.ProductId);
        }

        var result = product.Activate(timeProvider.GetUtcNow());

        if (result.IsError)
        {
            return result.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

internal sealed class DiscontinueProductCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<DiscontinueProductCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        DiscontinueProductCommand command,
        CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            return ProductErrors.NotFound(command.ProductId);
        }

        var result = product.Discontinue(timeProvider.GetUtcNow());

        if (result.IsError)
        {
            return result.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// Goods-in against a real delivery. This is the ONLY way stock increases outside a restore —
/// there is no automatic replenishment, because inventing inventory on a timer is what let
/// Onyx's oversells disappear before anyone could see them (D5 concealing D1).
/// </summary>
internal sealed class ReceiveStockCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<ReceiveStockCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ReceiveStockCommand command,
        CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            return ProductErrors.NotFound(command.ProductId);
        }

        var movement = product.Restore(
            command.Quantity,
            Domain.StockMovements.StockReason.Receipt,
            command.PurchaseOrderReference,
            timeProvider.GetUtcNow());

        if (movement.IsError)
        {
            return movement.Errors;
        }

        products.AddMovement(movement.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}
