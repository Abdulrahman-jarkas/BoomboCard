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
/// Claims one use of a promotion for one checkout session.
///
/// <para>
/// Three things have to be true at once, and each is enforced by a different mechanism —
/// deliberately, because each has a different race:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>The total cap</b>, by the aggregate's concurrency token. Two checkouts racing for the
/// last use both read the same count and both pass the check; the version predicate makes one
/// of them lose and be retried against the new count.
/// </item>
/// <item>
/// <b>The per-customer cap</b>, by the unique index on
/// <c>(PromotionId, CustomerId, CustomerOrdinal)</c>. The ordinal is computed from a read, so
/// two concurrent reservations compute the same one and collide at the database.
/// </item>
/// <item>
/// <b>The stacking policy</b>, by reading what the session already holds — which only works
/// because reservations are per session and the ledger can be asked.
/// </item>
/// </list>
///
/// <para>
/// Onyx had none of the three, because it had no reservation step: a promo code was a nullable
/// string copied onto the order, so a code with a "first 100 customers" limit had no limit, and
/// one customer could use the same code on every order they ever placed.
/// </para>
/// </summary>
internal sealed class ReservePromotionCommandHandler(
    IPromotionRepository promotions,
    IUnitOfWork<PromotionsDbContext> unitOfWork,
    TimeProvider timeProvider,
    ILogger<ReservePromotionCommandHandler> logger)
    : IRequestHandler<ReservePromotionCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ReservePromotionCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CheckoutSessionId))
        {
            return PromotionErrors.ReferenceRequired;
        }

        var promotion = await promotions.GetByIdAsync(command.PromotionId, cancellationToken);

        if (promotion is null)
        {
            return PromotionErrors.NotFound(command.PromotionId);
        }

        // Already reserved for this session? A retried checkout, or a redelivered message.
        // Return success rather than claiming a second use — the same idempotency-on-reference
        // decision Catalog's DeductStockCommand makes, and what makes retry the cheapest
        // recovery layer in the checkout saga.
        var existing = await promotions.GetSessionRedemptionAsync(
            promotion.Id, command.CheckoutSessionId, cancellationToken);

        if (existing is not null && !existing.IsReleased)
        {
            return Result.Success;
        }

        var sessionHoldings = await promotions.GetSessionRedemptionsAsync(
            command.CheckoutSessionId, cancellationToken);

        var stackingFailure = await CheckStackingAsync(
            promotion, sessionHoldings.Select(holding => holding.PromotionId).ToList(),
            cancellationToken);

        if (stackingFailure is not null)
        {
            return stackingFailure.Value;
        }

        var customerUses = await promotions.CountCustomerRedemptionsAsync(
            promotion.Id, command.CustomerId, cancellationToken);

        var redemption = promotion.Redeem(
            command.CustomerId,
            command.CheckoutSessionId,
            customerUses,
            timeProvider.GetUtcNow());

        if (redemption.IsError)
        {
            return redemption.Errors;
        }

        promotions.AddRedemption(redemption.Value);

        // One SaveChanges for the counter, the ledger row and the outbox messages the domain
        // events produce. If the unique index rejects the ordinal, the whole thing rolls back
        // and ResilientCommandDispatcher retries in a fresh scope against the new count.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Promotion {Code} reserved for session {SessionId}; {Count} use(s) now held.",
            promotion.Code.Value,
            command.CheckoutSessionId,
            promotion.RedemptionCount);

        return Result.Success;
    }

    /// <summary>
    /// Refuses the reservation when either this promotion or anything the session already holds
    /// is <see cref="StackingPolicy.Exclusive"/>.
    ///
    /// <para>
    /// Both directions, which is the part worth getting right. Checking only the incoming
    /// promotion would let an exclusive code already on the session be joined by a combinable
    /// one — and "exclusive" has to mean nothing else may join it, not merely that it may not
    /// join others.
    /// </para>
    /// </summary>
    private async Task<ErrorOr<Success>?> CheckStackingAsync(
        Promotion incoming,
        List<Guid> heldPromotionIds,
        CancellationToken cancellationToken)
    {
        if (heldPromotionIds.Count == 0)
        {
            return null;
        }

        if (incoming.Stacking is StackingPolicy.Exclusive)
        {
            return PromotionErrors.CannotCombine;
        }

        var held = await promotions.GetByIdsAsync(heldPromotionIds, cancellationToken);

        return held.Any(other => other.Stacking is StackingPolicy.Exclusive)
            ? PromotionErrors.CannotCombine
            : null;
    }
}
