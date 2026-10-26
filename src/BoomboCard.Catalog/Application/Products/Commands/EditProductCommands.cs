using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Application.Products.Commands;

/*
 * Onyx had no way to edit a product at all: no single-product create, no rename, no price
 * change, no delete (A32 and the capability gaps in section 7 of the issue register). Changing
 * a price required a developer and a redeploy.
 *
 * Each of these is a separate command rather than one "UpdateProduct" taking a bag of optional
 * fields. That is deliberate. A single update command cannot tell "set the name to null" from
 * "do not change the name", and it cannot be authorised per field — which is exactly how Onyx
 * S3 worked: PATCH /api/customers/{id} bound the whole entity and let a shopper set their own
 * Role to Admin.
 */

public sealed record RenameProductCommand(Guid ProductId, string Name)
    : IRequest<ErrorOr<Success>>, ICatalogRequest;

public sealed record SetProductCostPriceCommand(Guid ProductId, string CountryCode, decimal Amount)
    : IRequest<ErrorOr<Success>>, ICatalogRequest;

public sealed record WithdrawProductPriceCommand(Guid ProductId, string CountryCode)
    : IRequest<ErrorOr<Success>>, ICatalogRequest;

internal sealed class RenameProductCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<RenameProductCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RenameProductCommand command,
        CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            return ProductErrors.NotFound(command.ProductId);
        }

        var result = product.Rename(command.Name, timeProvider.GetUtcNow());

        if (result.IsError)
        {
            return result.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// The cost price is what the product costs us. Onyx had no such field, so margin was
/// unknowable — and its 5% volume discount and promo codes were handed out with no visibility
/// of whether they were profitable.
///
/// It takes a country code purely to derive the currency, so a cost can never be stored in a
/// currency we do not trade in.
/// </summary>
internal sealed class SetProductCostPriceCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<SetProductCostPriceCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        SetProductCostPriceCommand command,
        CancellationToken cancellationToken)
    {
        var country = Country.FromCode(command.CountryCode);

        if (country.IsError)
        {
            return country.Errors;
        }

        var amount = Money.Create(command.Amount, country.Value.CurrencyCode);

        if (amount.IsError)
        {
            return amount.Errors;
        }

        var product = await products.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            return ProductErrors.NotFound(command.ProductId);
        }

        var result = product.ChangeCostPrice(amount.Value, timeProvider.GetUtcNow());

        if (result.IsError)
        {
            return result.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// Stops selling in one country without discontinuing the product everywhere. The aggregate
/// refuses if it would leave an active product with no price at all.
/// </summary>
internal sealed class WithdrawProductPriceCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<WithdrawProductPriceCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        WithdrawProductPriceCommand command,
        CancellationToken cancellationToken)
    {
        var country = Country.FromCode(command.CountryCode);

        if (country.IsError)
        {
            return country.Errors;
        }

        var product = await products.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            return ProductErrors.NotFound(command.ProductId);
        }

        var result = product.WithdrawPrice(country.Value, timeProvider.GetUtcNow());

        if (result.IsError)
        {
            return result.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}
