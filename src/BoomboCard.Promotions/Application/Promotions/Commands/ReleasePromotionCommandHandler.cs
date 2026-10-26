using BoomboCard.Promotions.Contracts;
using BoomboCard.Promotions.Data;
using BoomboCard.Promotions.Data.Repositories;
using BoomboCard.Promotions.Domain.PromotionAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Promotions.Application.Promotions.Commands;

/// <summary>
/// Gives back every use a checkout session was holding.
///
/// <para>
/// <b>Safe to call unconditionally</b>, which is the whole design requirement. Sales' expiry
/// sweeper is told a session id and fires this blind — it does not know, and should not have to
/// ask, whether that session ever reserved a promotion. A session holding nothing is a success
/// returning zero releases, exactly as <c>RestoreStockCommand</c> is a no-op when no stock was
/// held.
/// </para>
///
/// <para>
/// That property is why this is the layer the checkout saga actually relies on. Inline
/// compensation can fail for the same reason the step it is compensating for failed; a sweeper
/// that can be run repeatedly with no extra effect cannot.
/// </para>
///
/// <para>
/// <b>It releases every promotion on the session, not one named promotion.</b> The caller knows
/// the session, and making it enumerate which promotions were on it would mean Sales keeping its
/// own copy of that list — two modules holding the same fact, which is how Onyx's promo expiry
/// came to be written in two files with two different dates (<b>M14</b>).
/// </para>
/// </summary>
internal sealed class ReleasePromotionCommandHandler(
    IPromotionRepository promotions,
    IUnitOfWork<PromotionsDbContext> unitOfWork,
    TimeProvider timeProvider,
    ILogger<ReleasePromotionCommandHandler> logger)
    : IRequestHandler<ReleasePromotionCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ReleasePromotionCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CheckoutSessionId))
        {
            return PromotionErrors.ReferenceRequired;
        }

        var nowUtc = timeProvider.GetUtcNow();

        var holdings = await promotions.GetSessionRedemptionsAsync(
            command.CheckoutSessionId, cancellationToken);

        if (holdings.Count == 0)
        {
            // Nothing held. Not an error — see the summary.
            return Result.Success;
        }

        var affected = await promotions.GetByIdsAsync(
            holdings.Select(holding => holding.PromotionId).Distinct().ToList(),
            cancellationToken);

        var byId = affected.ToDictionary(promotion => promotion.Id);

        foreach (var holding in holdings)
        {
            if (!byId.TryGetValue(holding.PromotionId, out var promotion))
            {
                // A ledger row whose promotion is gone. The row is still marked released, so the
                // sweeper does not keep finding it forever — but it is logged, because a missing
                // promotion means something deleted a row that should only ever be closed.
                logger.LogError(
                    "Redemption {RedemptionId} references promotion {PromotionId}, which no "
                    + "longer exists. Releasing the ledger row anyway.",
                    holding.Id,
                    holding.PromotionId);

                holding.Release(command.Reason, nowUtc);
                continue;
            }

            // The aggregate decrements its counter and raises the event; the ledger row keeps
            // its history and is marked rather than deleted, so reporting can tell "100 people
            // redeemed this" from "140 tried and 40 abandoned".
            promotion.ReturnUse(
                holding.CustomerId, holding.CheckoutSessionId, command.Reason, nowUtc);

            holding.Release(command.Reason, nowUtc);
        }

        // One SaveChanges for every counter, every ledger row and every outbox message.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Released {Count} promotion use(s) held by session {SessionId}: {Reason}.",
            holdings.Count,
            command.CheckoutSessionId,
            command.Reason);

        return Result.Success;
    }
}
