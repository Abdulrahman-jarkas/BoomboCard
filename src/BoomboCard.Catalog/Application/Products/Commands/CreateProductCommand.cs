using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Application.Products.Commands;


/// <summary>
/// Creates a product in <c>Draft</c>. It cannot be sold until it has a price in at least one
/// country and has been activated.
///
/// <c>Sku</c> is normalised to upper case, so it is unique regardless of how it was typed.
/// <c>InitialStock</c> records an opening-balance movement, so the audit log explains the
/// quantity from the very first row.
/// </summary>
public sealed record CreateProductCommand(
    string Sku,
    string Name,
    int InitialStock,
    int? LowStockThreshold = null) : IRequest<ErrorOr<Guid>>, ICatalogRequest;

internal sealed class CreateProductCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<CreateProductCommand, ErrorOr<Guid>>
{
    public async Task<ErrorOr<Guid>> Handle(
        CreateProductCommand command,
        CancellationToken cancellationToken)
    {
        var sku = Sku.Create(command.Sku);

        if (sku.IsError)
        {
            return sku.Errors;
        }

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return ProductErrors.NameRequired;
        }

        if (command.Name.Trim().Length > Product.MaxNameLength)
        {
            return ProductErrors.NameTooLong;
        }


        var stock = StockLevel.Create(command.InitialStock);

        if (stock.IsError)
        {
            return stock.Errors;
        }

        // Checked here for a clean error message; the unique index is what actually guarantees
        // it under concurrency.
        if (await products.SkuExistsAsync(sku.Value, cancellationToken))
        {
            return ProductErrors.SkuAlreadyExists(sku.Value.Value);
        }

        var product = Product.Create(
            sku.Value,
            command.Name,
            stock.Value,
            timeProvider.GetUtcNow(),
            command.LowStockThreshold ?? Product.DefaultLowStockThreshold);

        products.Add(product);

        // Opening balance, so SUM(StockMovement.Delta) == Product.StockQuantity holds from the
        // first row. Without it the reconciliation invariant is broken for every product that
        // is created with stock already on hand.
        if (command.InitialStock > 0)
        {
            products.AddMovement(StockMovement.Create(
                product.Id,
                command.InitialStock,
                StockReason.Adjustment,
                StockReferences.OpeningBalance,
                command.InitialStock,
                timeProvider.GetUtcNow()));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return product.Id;
    }
}
