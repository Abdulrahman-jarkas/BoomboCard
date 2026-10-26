using BoomboCard.Loyalty.Contracts;
using BoomboCard.Loyalty.Data;
using BoomboCard.Loyalty.Data.Repositories;
using BoomboCard.Loyalty.Domain.Ledger;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Loyalty.Application.Points.Commands;

/// <summary>
/// Credits points earned on an order. Returns the number credited.
/// </summary>
internal sealed class GrantPointsCommandHandler(
    IPointsRepository points,
    IUnitOfWork<LoyaltyDbContext> unitOfWork,
    TimeProvider timeProvider,
    ILogger<GrantPointsCommandHandler> logger)
    : IRequestHandler<GrantPointsCommand, ErrorOr<int>>
{
    public async Task<ErrorOr<int>> Handle(
        GrantPointsCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.OrderReference))
        {
            return PointsErrors.ReferenceRequired;
        }

        var spend = Money.Create(command.QualifyingSpend, command.CurrencyCode);

        if (spend.IsError)
        {
            return spend.Errors;
        }

        var account = await points.GetByCustomerAsync(command.CustomerId, cancellationToken);

        if (account is null)
        {
            return PointsErrors.AccountNotFound(command.CustomerId);
        }

        // Already credited? A redelivered message, or a retried call. Return the same answer
        // rather than crediting twice — UX_PointsLedger_Idempotency is the backstop, this is
        // the fast path. Same division of labour as everywhere else here.
        if (await points.EntryExistsAsync(
                account.Id, command.OrderReference, PointsReason.Earned, cancellationToken))
        {
            return await points.PointsEarnedAgainstAsync(
                account.Id, command.OrderReference, cancellationToken);
        }

        // The rule, applied in one place. Onyx M12: nothing anywhere incremented the balance,
        // so the scheme had a spend side and no earn side.
        var earned = EarnRule.PointsFor(spend.Value);

        if (earned == 0)
        {
            // A sub-unit order earns nothing. Not an error — and not a ledger row either, since
            // a zero-delta entry would be a statement line saying nothing happened.
            logger.LogDebug(
                "Order {Reference} of {Spend} earned no points at the configured rate.",
                command.OrderReference,
                spend.Value);

            return 0;
        }

        var entry = account.Earn(earned, command.OrderReference, timeProvider.GetUtcNow());

        if (entry.IsError)
        {
            return entry.Errors;
        }

        points.AddEntry(entry.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return earned;
    }
}

/// <summary>
/// Debits points a checkout is spending. The half of <b>D4</b> that commits with the order.
/// </summary>
internal sealed class RedeemPointsCommandHandler(
    IPointsRepository points,
    IUnitOfWork<LoyaltyDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<RedeemPointsCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RedeemPointsCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CheckoutSessionId))
        {
            return PointsErrors.ReferenceRequired;
        }

        var account = await points.GetByCustomerAsync(command.CustomerId, cancellationToken);

        if (account is null)
        {
            return PointsErrors.AccountNotFound(command.CustomerId);
        }

        // A retried checkout debits once.
        if (await points.EntryExistsAsync(
                account.Id, command.CheckoutSessionId, PointsReason.Redeemed, cancellationToken))
        {
            return Result.Success;
        }

        // Re-checks sufficiency, because the quote was shown earlier and the customer may have
        // spent points elsewhere since. The aggregate's concurrency token is what makes THIS
        // check safe against a simultaneous one.
        var entry = account.Redeem(
            command.Points, command.CheckoutSessionId, timeProvider.GetUtcNow());

        if (entry.IsError)
        {
            return entry.Errors;
        }

        points.AddEntry(entry.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// Gives back the points a checkout session spent.
///
/// <para>
/// Safe to call unconditionally, and takes a session id rather than a customer id — Sales'
/// expiry sweeper is told a session and fires blind. A session that redeemed nothing is a
/// success doing nothing, exactly as <c>RestoreStockCommand</c> and
/// <c>ReleasePromotionCommand</c> are.
/// </para>
/// </summary>
internal sealed class ReleasePointsCommandHandler(
    IPointsRepository points,
    IUnitOfWork<LoyaltyDbContext> unitOfWork,
    TimeProvider timeProvider,
    ILogger<ReleasePointsCommandHandler> logger)
    : IRequestHandler<ReleasePointsCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ReleasePointsCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.CheckoutSessionId))
        {
            return PointsErrors.ReferenceRequired;
        }

        var redemptions = await points.GetRedemptionsForSessionAsync(
            command.CheckoutSessionId, cancellationToken);

        if (redemptions.Count == 0)
        {
            return Result.Success;
        }

        var nowUtc = timeProvider.GetUtcNow();
        var returned = 0;

        foreach (var redemption in redemptions)
        {
            var account = await points.GetByIdAsync(redemption.AccountId, cancellationToken);

            if (account is null)
            {
                logger.LogError(
                    "Ledger entry {EntryId} references account {AccountId}, which no longer "
                    + "exists. Points cannot be returned.",
                    redemption.Id,
                    redemption.AccountId);

                continue;
            }

            // The return's reference distinguishes it from the redemption it reverses, so both
            // rows coexist under UX_PointsLedger_Idempotency and the statement reads as a pair.
            // It also makes the release itself idempotent: a second sweep finds the Returned
            // entry already present.
            var reference = $"{command.CheckoutSessionId}:returned";

            if (await points.EntryExistsAsync(
                    account.Id, reference, PointsReason.Returned, cancellationToken))
            {
                continue;
            }

            var entry = account.Return(-redemption.Delta, reference, nowUtc);

            if (entry.IsError)
            {
                logger.LogError(
                    "Could not return {Points} points to account {AccountId}: {Error}",
                    -redemption.Delta,
                    account.Id,
                    entry.FirstError.Description);

                continue;
            }

            points.AddEntry(entry.Value);
            returned += -redemption.Delta;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (returned > 0)
        {
            logger.LogInformation(
                "Returned {Points} points held by session {SessionId}: {Reason}.",
                returned,
                command.CheckoutSessionId,
                command.Reason);
        }

        return Result.Success;
    }
}

/// <summary>
/// Takes back points an order earned, because that order was refunded.
/// </summary>
internal sealed class ReverseEarnedPointsCommandHandler(
    IPointsRepository points,
    IUnitOfWork<LoyaltyDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<ReverseEarnedPointsCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ReverseEarnedPointsCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.OrderReference)
            || string.IsNullOrWhiteSpace(command.RefundReference))
        {
            return PointsErrors.ReferenceRequired;
        }

        var account = await points.GetByCustomerAsync(command.CustomerId, cancellationToken);

        if (account is null)
        {
            return PointsErrors.AccountNotFound(command.CustomerId);
        }

        if (await points.EntryExistsAsync(
                account.Id, command.RefundReference, PointsReason.Reversed, cancellationToken))
        {
            return Result.Success;
        }

        // Bounded by what the earning actually gave, read from the ledger rather than taken
        // from the caller — the caller knows the order, the ledger knows what it was worth.
        var earned = await points.PointsEarnedAgainstAsync(
            account.Id, command.OrderReference, cancellationToken);

        if (earned <= 0)
        {
            return PointsErrors.NothingToReverse;
        }

        var entry = account.ReverseEarning(
            earned, command.RefundReference, timeProvider.GetUtcNow());

        if (entry.IsError)
        {
            return entry.Errors;
        }

        points.AddEntry(entry.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// A manual correction or goodwill grant, attributed to the administrator making it.
/// </summary>
internal sealed class AdjustPointsCommandHandler(
    IPointsRepository points,
    IUnitOfWork<LoyaltyDbContext> unitOfWork,
    Customers.Contracts.ICurrentActor actor,
    TimeProvider timeProvider,
    ILogger<AdjustPointsCommandHandler> logger)
    : IRequestHandler<AdjustPointsCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        AdjustPointsCommand command,
        CancellationToken cancellationToken)
    {
        // From the authenticated principal, never the request. An actor a caller supplies is one
        // a caller can forge, and the only reason to record one is to trust it afterwards.
        if (actor.CustomerId is not { } actorId)
        {
            return PointsErrors.ActorRequired;
        }

        var account = await points.GetByCustomerAsync(command.CustomerId, cancellationToken);

        if (account is null)
        {
            return PointsErrors.AccountNotFound(command.CustomerId);
        }

        var entry = account.Adjust(
            command.Points, actorId, command.Note, timeProvider.GetUtcNow());

        if (entry.IsError)
        {
            return entry.Errors;
        }

        points.AddEntry(entry.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Points balance of {CustomerId} adjusted by {Points} by {ActorId}: {Note}",
            command.CustomerId,
            command.Points,
            actorId,
            command.Note);

        return Result.Success;
    }
}
