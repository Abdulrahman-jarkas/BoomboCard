using BoomboCard.Customers.Contracts.IntegrationEvents;
using BoomboCard.Loyalty.Data;
using BoomboCard.Loyalty.Data.Repositories;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate;
using BoomboCard.SharedKernel;
using BoomboCard.SharedKernel.Persistence.Inbox;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Loyalty.Application.Points.IntegrationEvents;

/*
 * The first real cross-module consumers in the codebase, and therefore the first actual use of
 * the inbox built in Phase 0.
 *
 * Both follow the same three-step shape, and all three steps matter:
 *
 *   1. Claim the event for this consumer, or return. The claim is ADDED, not saved — it commits
 *      with the work below, so "this was consumed" and "here is what consuming it did" cannot
 *      disagree. See IInboxGuard.
 *   2. Do the work.
 *   3. One SaveAndDispatchAsync, which writes the claim, the aggregate and the ledger row
 *      together, and drains the domain events the aggregate raised.
 *
 * Step 3 is SaveAndDispatchAsync rather than SaveChangesAsync because an integration event
 * handler is NOT dispatched through the MediatR request pipeline — OutboxDispatcher publishes
 * it as a notification, so no transaction is open and nothing would drain the events. That is
 * exactly bug 10, and the guard in SaveChangesAsync now throws rather than letting it pass
 * silently.
 */

/// <summary>
/// Opens a points account when a customer registers.
///
/// <para>
/// Driven by an event rather than created on demand, because the account's existence is a fact
/// about the customer rather than a side effect of their first order — and because a customer
/// should be able to see "0 points" rather than "no account", which is a different and more
/// confusing thing.
/// </para>
///
/// <para>
/// Delivery is at-least-once, so this runs twice sometimes. Two guards, deliberately: the inbox
/// claim catches the ordinary redelivery, and <c>UX_PointsAccounts_CustomerId</c> catches the
/// case where two deliveries race past the claim's read. The constraint is the guarantee; the
/// claim is the fast path.
/// </para>
/// </summary>
internal sealed class OnCustomerRegisteredOpenPointsAccount(
    IPointsRepository points,
    IUnitOfWork<LoyaltyDbContext> unitOfWork,
    IInboxGuard<LoyaltyDbContext> inbox,
    TimeProvider timeProvider,
    ILogger<OnCustomerRegisteredOpenPointsAccount> logger)
    : INotificationHandler<CustomerRegisteredIntegrationEvent>
{
    public async Task Handle(
        CustomerRegisteredIntegrationEvent notification,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, GetType(), cancellationToken))
        {
            return;
        }

        if (await points.AccountExistsAsync(notification.CustomerId, cancellationToken))
        {
            // Claimed but already done — a previous delivery got as far as the account and not
            // as far as the claim. Saving the claim alone settles it.
            await unitOfWork.SaveAndDispatchAsync(cancellationToken);
            return;
        }

        points.Add(PointsAccount.Open(notification.CustomerId, timeProvider.GetUtcNow()));

        await unitOfWork.SaveAndDispatchAsync(cancellationToken);

        logger.LogInformation(
            "Opened a points account for customer {CustomerId}.", notification.CustomerId);
    }
}

/// <summary>
/// Closes a points account when its customer is anonymised, forfeiting whatever remains.
///
/// <para>
/// This is the other half of an erasure request, and the reason
/// <c>CustomerAnonymisedIntegrationEvent</c> exists at all: Customers can overwrite its own
/// name and address columns, and it cannot reach into another module's database. Onyx <b>D13</b>
/// deleted the customer row and left every copy standing elsewhere.
/// </para>
///
/// <para>
/// Note what is <b>not</b> erased: the ledger. It is a financial record of value given and
/// taken, and it holds a customer id and nothing else personal — so it survives, while the
/// balance goes to zero through a recorded <c>Forfeited</c> entry rather than being abandoned at
/// whatever it happened to be.
/// </para>
/// </summary>
internal sealed class OnCustomerAnonymisedClosePointsAccount(
    IPointsRepository points,
    IUnitOfWork<LoyaltyDbContext> unitOfWork,
    IInboxGuard<LoyaltyDbContext> inbox,
    TimeProvider timeProvider,
    ILogger<OnCustomerAnonymisedClosePointsAccount> logger)
    : INotificationHandler<CustomerAnonymisedIntegrationEvent>
{
    public async Task Handle(
        CustomerAnonymisedIntegrationEvent notification,
        CancellationToken cancellationToken)
    {
        if (!await inbox.TryClaimAsync(notification, GetType(), cancellationToken))
        {
            return;
        }

        var account = await points.GetByCustomerAsync(notification.CustomerId, cancellationToken);

        if (account is null || account.IsClosed)
        {
            await unitOfWork.SaveAndDispatchAsync(cancellationToken);
            return;
        }

        var forfeited = account.Balance;
        var entry = account.Close($"anonymised:{notification.CustomerId:n}", timeProvider.GetUtcNow());

        if (entry.IsError)
        {
            // Logged, not thrown. Throwing would fail the whole outbox message and have it
            // retried forever against a state that will not change — and a stuck erasure is a
            // compliance problem that needs a human, not a retry loop.
            logger.LogError(
                "Could not close the points account for anonymised customer {CustomerId}: {Error}",
                notification.CustomerId,
                entry.FirstError.Description);

            return;
        }

        points.AddEntry(entry.Value);

        await unitOfWork.SaveAndDispatchAsync(cancellationToken);

        logger.LogInformation(
            "Closed the points account for anonymised customer {CustomerId}, forfeiting "
            + "{Points} points.",
            notification.CustomerId,
            forfeited);
    }
}
