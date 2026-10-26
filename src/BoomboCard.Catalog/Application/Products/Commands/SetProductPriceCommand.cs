using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Catalog.Application.Products.Commands;

/// <summary>
/// Sets or changes a product's price in one country.
///
/// Onyx had no way to change a price: not individually, and not in bulk (its import only ever
/// inserted). A price change required a developer and a redeploy.
///
/// <paramref name="EffectiveFrom"/> defaults to now but can be future-dated, which is how a
/// scheduled price change works without a job mutating rows at midnight.
/// </summary>
public sealed record SetProductPriceCommand(
    Guid ProductId,
    string CountryCode,
    decimal Amount,
    DateTimeOffset? EffectiveFrom = null) : IRequest<ErrorOr<Success>>, ICatalogRequest;

internal sealed class SetProductPriceCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<SetProductPriceCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        SetProductPriceCommand command,
        CancellationToken cancellationToken)
    {
        var country = Country.FromCode(command.CountryCode);

        if (country.IsError)
        {
            return country.Errors;
        }

        // The currency is derived from the country, never supplied by the caller — so a price
        // can never be stored in a currency that country does not trade in.
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

        var nowUtc = timeProvider.GetUtcNow();

        var result = product.SetPrice(
            country.Value,
            amount.Value,
            command.EffectiveFrom ?? nowUtc,
            nowUtc);

        if (result.IsError)
        {
            return result.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}
