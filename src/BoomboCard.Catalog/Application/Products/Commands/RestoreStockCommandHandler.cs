using BoomboCard.Catalog.Contracts;
using BoomboCard.Catalog.Data.Repositories;
using BoomboCard.Catalog.Data;
using BoomboCard.Catalog.Domain.ProductAggregate;
using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Catalog.Application.Products.Commands;

/// <summary>
/// Puts stock back after a checkout did not complete.
///
/// <para>
/// <b>It cannot put back more than was taken.</b> That clamp is the whole substance of this
/// handler, and it was added after the Sales expiry sweeper restored two units against a session
/// that had never deducted anything — stock conjured from nothing, which is Onyx <b>D5</b>'s
/// failure mode arriving in our own code by a different road. BUGS-AND-LESSONS section 13.
/// </para>
///
/// <para>
/// Idempotency did not catch it and could not have: the unique index on
/// <c>(ProductId, Reference, Reason)</c> stops an operation being <i>repeated</i>, and this was
/// an operation being performed <i>wrongly</i> the first time. The guarantee that is actually
/// needed is Catalog's own invariant — <b>never invent stock</b> — and the only way to hold it
/// is for Catalog to check, because a caller that knows what it held is a caller being trusted.
/// </para>
///
/// <para>
/// <c>Return</c> is deliberately exempt: a return references an order and real goods coming back
/// through the door, so there is no prior deduction under that reference to match it against.
/// </para>
/// </summary>
internal sealed class RestoreStockCommandHandler(
    IProductRepository products,
    IUnitOfWork<CatalogDbContext> unitOfWork,
    TimeProvider timeProvider,
    ILogger<RestoreStockCommandHandler> logger)
    : IRequestHandler<RestoreStockCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RestoreStockCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Lines.Count == 0)
        {
            return Error.Validation("RestoreStock.NoLines", "At least one line is required.");
        }

        if (string.IsNullOrWhiteSpace(command.Reference))
        {
            return ProductErrors.ReferenceRequired;
        }

        var nowUtc = timeProvider.GetUtcNow();
        var reason = Map(command.Reason);

        var requested = command.Lines
            .GroupBy(line => line.ProductId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));

        // This handler is reached from an integration event, and outbox delivery is
        // at-least-once — so being called twice is expected, not exceptional. Without this
        // check a redelivered "payment failed" would inflate stock.
        var alreadyRestored = await products.MovementExistsAsync(
            requested.Keys.First(), command.Reference, reason, cancellationToken);

        if (alreadyRestored)
        {
            return Result.Success;
        }

        // THE GUARD. How much is actually still held against this reference, per product,
        // computed from the movement log rather than taken from the caller.
        //
        // A release fires blind by design — the sweeper is handed a session id and does not know
        // whether anything was deducted — so "nothing was held" has to be a success returning
        // nothing rather than an error. Clamping gives exactly that, and a request for more than
        // was held is logged rather than silently trimmed.
        var held = command.Reason is RestoreReason.Return
            ? null
            : await products.GetHeldQuantitiesAsync(
                command.Reference, requested.Keys.ToList(), cancellationToken);

        if (held is not null)
        {
            foreach (var (productId, quantity) in requested.ToList())
            {
                var available = held.GetValueOrDefault(productId);

                if (quantity <= available)
                {
                    continue;
                }

                if (available == 0)
                {
                    logger.LogWarning(
                        "A restore of {Quantity} was requested for product {ProductId} against "
                        + "reference {Reference}, which holds none. Nothing restored — stock is "
                        + "never created from a release.",
                        quantity,
                        productId,
                        command.Reference);

                    requested.Remove(productId);
                    continue;
                }

                logger.LogWarning(
                    "A restore of {Quantity} was requested for product {ProductId} against "
                    + "reference {Reference}, which holds only {Available}. Restoring what is "
                    + "held — stock is never created from a release.",
                    quantity,
                    productId,
                    command.Reference,
                    available);

                requested[productId] = available;
            }
        }

        if (requested.Count == 0)
        {
            // Nothing was held under this reference. A success, not an error: that is precisely
            // the case the sweeper's unconditional fire relies on.
            return Result.Success;
        }

        var loaded = await products.GetByIdsAsync(requested.Keys.ToList(), cancellationToken);
        var byId = loaded.ToDictionary(product => product.Id);

        var movements = new List<StockMovement>(requested.Count);

        foreach (var (productId, quantity) in requested)
        {
            if (!byId.TryGetValue(productId, out var product))
            {
                // A product deleted between deduction and restore would strand stock
                // permanently. Fail loudly so the outbox retries and eventually dead-letters
                // it into view, rather than silently dropping the restore.
                return ProductErrors.NotFound(productId);
            }

            var movement = product.Restore(quantity, reason, command.Reference, nowUtc);

            if (movement.IsError)
            {
                return movement.Errors;
            }

            movements.Add(movement.Value);
        }

        products.AddMovements(movements);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }

    private static StockReason Map(RestoreReason reason) =>
        reason switch
        {
            RestoreReason.PaymentFailed => StockReason.PaymentFailed,
            RestoreReason.SessionExpired => StockReason.SessionExpired,
            RestoreReason.OrderCancelled => StockReason.OrderCancelled,
            RestoreReason.Return => StockReason.Return,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unmapped restore reason.")
        };
}
