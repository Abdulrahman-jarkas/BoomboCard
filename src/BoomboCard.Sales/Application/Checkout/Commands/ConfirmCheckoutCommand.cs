using BoomboCard.Catalog.Contracts;
using BoomboCard.Loyalty.Contracts;
using BoomboCard.Promotions.Contracts;
using BoomboCard.Sales.Contracts;
using BoomboCard.Sales.Data;
using BoomboCard.Sales.Data.Repositories;
using BoomboCard.Sales.Domain.CheckoutSessionAggregate;
using BoomboCard.Sales.Domain.OrderAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Sales.Application.Checkout.Commands;

/// <summary>What a confirmed checkout returns. No money inbound, and no money invented outbound.</summary>
public sealed record ConfirmedCheckoutResponse(
    Guid OrderId,
    string OrderNumber,
    string Status,
    decimal Total,
    string CurrencyCode);

/// <summary>
/// Confirms a checkout: holds the stock, the promotion use and the points, then creates the
/// order. <b>TX 1 and TX 2 of the flow.</b>
/// </summary>
public sealed record ConfirmCheckoutCommand(Guid CheckoutSessionId)
    : IRequest<ErrorOr<ConfirmedCheckoutResponse>>, ISalesRequest;

/// <summary>
/// The saga, and the single most carefully ordered method in the codebase.
///
/// <code>
/// TX 1  DeductStockCommand(reference: sessionId)      -> Catalog     oversell now impossible
///       ReservePromotionCommand(sessionId)            -> Promotions  cap now enforced
///       RedeemPointsCommand(sessionId)                -> Loyalty     balance now debited
/// TX 2  session.BeginHolding() + session.Confirm() + Order + outbox  -> 202 Accepted
/// </code>
///
/// <para>
/// <b>Why TX 1 is three separate commits in three modules rather than one transaction.</b> An
/// invariant never crosses a boundary. Stock correctness is Catalog's alone, the usage cap is
/// Promotions', the balance is Loyalty's — and each enforces its own under its own concurrency
/// token, on its own connection. A distributed transaction would be the wrong tool even if it
/// were available: it would couple three modules' availability together and still not make any
/// of their invariants each other's business.
/// </para>
///
/// <para>
/// <b>Four layers of recovery, cheapest first</b> (CONCEPTS §13):
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Retry.</b> Every step is idempotent on the session id, so re-sending this whole command is
/// safe and free. It is also the first thing this handler does — if an order already exists for
/// the session, it returns it.
/// </item>
/// <item>
/// <b>Inline compensation.</b> Best effort, below, and <b>never relied upon</b>: it can fail for
/// the same reason the step it is compensating for failed.
/// </item>
/// <item>
/// <b>The expiry sweeper.</b> The actual guarantee. Fires release unconditionally against every
/// live session past its deadline; idempotency makes it a no-op when nothing was held.
/// </item>
/// <item>
/// <b>Orphan reconciliation.</b> Stock holds older than the session lifetime whose session is
/// not confirmed. Should find nothing, and alerts when it does.
/// </item>
/// </list>
///
/// <para>
/// Onyx had none of this, because it had no session: <c>OrdersController.Create</c> created the
/// order, saved, then deducted stock line by line with a <c>SaveChangesAsync</c> inside the loop,
/// returning HTTP 200 with <c>{ error: "Not enough stock for X" }</c> partway through and leaving
/// the earlier deductions committed against a half-built order with a total of zero
/// (<b>D2</b>, <b>D3</b>, <b>M19</b>, <b>A23</b>).
/// </para>
/// </summary>
internal sealed class ConfirmCheckoutCommandHandler(
    ISalesRepository sales,
    IUnitOfWork<SalesDbContext> unitOfWork,
    ICommandDispatcher dispatcher,
    TimeProvider timeProvider,
    ILogger<ConfirmCheckoutCommandHandler> logger)
    : IRequestHandler<ConfirmCheckoutCommand, ErrorOr<ConfirmedCheckoutResponse>>
{
    public async Task<ErrorOr<ConfirmedCheckoutResponse>> Handle(
        ConfirmCheckoutCommand command,
        CancellationToken cancellationToken)
    {
        // ---- recovery layer 0: retry.
        //
        // An order already exists for this session, so this is a re-sent confirm. Return the
        // same answer rather than doing anything. UX_Orders_CheckoutSession is the backstop;
        // this is the fast path, and it is what makes "just send it again" a legitimate recovery
        // instruction rather than a way to place two orders.
        var existing = await sales.GetOrderBySessionAsync(command.CheckoutSessionId, cancellationToken);

        if (existing is not null)
        {
            return Describe(existing);
        }

        var session = await sales.GetSessionAsync(command.CheckoutSessionId, cancellationToken);

        if (session is null)
        {
            return CheckoutErrors.NotFound(command.CheckoutSessionId);
        }

        var nowUtc = timeProvider.GetUtcNow();

        if (session.Status.IsOpen && session.HasExpiredAt(nowUtc))
        {
            return CheckoutErrors.Expired;
        }

        if (!session.Status.IsLive)
        {
            return CheckoutStatusErrors.NotLive;
        }

        // The guard that would have caught M19. A quote whose total does not equal its rows is a
        // defect, and refusing to charge for it beats charging a figure nothing explains.
        if (!session.TotalMatchesItsRows())
        {
            logger.LogError(
                "Checkout {SessionId} has a total of {Total} that does not match its rows. "
                + "Refusing to confirm.",
                session.Id,
                session.Total);

            return CheckoutErrors.TotalDoesNotMatchItsRows;
        }

        // ================================================================ TX 1
        //
        // Three commits in three modules, each through ICommandDispatcher so each gets its own
        // fresh scope, its own transaction on its own context, and the concurrency retry. The
        // reference is the session id in every case, which is what lets one release call per
        // module find everything later.
        var reference = session.HoldReference;

        var stockHeld = await HoldStockAsync(session, reference, cancellationToken);

        if (stockHeld.IsError)
        {
            // Nothing was held, so there is nothing to compensate. The session stays Open and
            // the sweeper will expire it in due course.
            return stockHeld.Errors;
        }

        if (session.PromotionId is { } promotionId)
        {
            var reserved = await dispatcher.SendAsync(
                new ReservePromotionCommand(promotionId, session.CustomerId, reference),
                cancellationToken);

            if (reserved.IsError)
            {
                await CompensateAsync(session, reference, "promotion reservation failed", cancellationToken);
                return reserved.Errors;
            }
        }

        if (session.PointsRedeemed > 0)
        {
            var redeemed = await dispatcher.SendAsync(
                new RedeemPointsCommand(session.CustomerId, session.PointsRedeemed, reference),
                cancellationToken);

            if (redeemed.IsError)
            {
                await CompensateAsync(session, reference, "points redemption failed", cancellationToken);
                return redeemed.Errors;
            }
        }

        // ================================================================ TX 2
        //
        // One transaction, owned by SalesUnitOfWorkBehavior: the session moves to Holding and
        // then Confirmed, the order is created from it, the number is allocated under its own
        // concurrency token, and the outbox rows the domain events produce all commit together.
        //
        // If this throws, layers 2 and 3 clean up. The compensation below is only attempted for
        // the in-process failures above, where we still know what we held.
        var holding = session.BeginHolding(nowUtc);

        if (holding.IsError)
        {
            await CompensateAsync(session, reference, "session could not begin holding", cancellationToken);
            return holding.Errors;
        }

        var sequence = await sales.GetOrCreateSequenceAsync(cancellationToken);
        var number = sequence.Allocate();

        // Place the order FIRST, then confirm the session with its id. The other way round means
        // confirming before there is an id to confirm with — see Order.PlaceFrom.
        var order = Order.PlaceFrom(session, number, nowUtc);

        if (order.IsError)
        {
            await CompensateAsync(session, reference, "order could not be placed", cancellationToken);
            return order.Errors;
        }

        var confirmed = session.Confirm(order.Value.Id, nowUtc);

        if (confirmed.IsError)
        {
            await CompensateAsync(session, reference, "session could not be confirmed", cancellationToken);
            return confirmed.Errors;
        }

        sales.Add(order.Value);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Order {OrderNumber} placed from checkout {SessionId} for {Total}.",
            number.Value,
            session.Id,
            order.Value.Total);

        return Describe(order.Value);
    }

    /// <summary>
    /// TX 1's first step. Separate because it is the one that most often fails for a business
    /// reason, and that reason needs translating into something a customer can read.
    /// </summary>
    private async Task<ErrorOr<Success>> HoldStockAsync(
        CheckoutSession session,
        string reference,
        CancellationToken cancellationToken)
    {
        var lines = session.Lines
            .Select(line => new StockLine(line.ProductId, line.Quantity))
            .ToList();

        // One command for every line. Catalog loads every product, deducts each and saves once,
        // so a three-line order cannot half-succeed — which is exactly what Onyx D2 did by
        // looping and saving per line.
        var deducted = await dispatcher.SendAsync(
            new DeductStockCommand(reference, lines), cancellationToken);

        if (!deducted.IsError)
        {
            return Result.Success;
        }

        // "InsufficientStock" is the expected outcome, not an exception — out-of-stock is the
        // most common answer at checkout. Translated into one customer-facing message rather
        // than leaking Catalog's wording, and returned as a 409 rather than Onyx's HTTP 200
        // with an error inside it (A23).
        logger.LogInformation(
            "Checkout {SessionId} could not hold stock: {Error}",
            session.Id,
            deducted.FirstError.Code);

        return deducted.FirstError.Code.Contains("InsufficientStock", StringComparison.Ordinal)
            ? CheckoutErrors.InsufficientStock
            : deducted.Errors;
    }

    /// <summary>
    /// Recovery layer 1: best-effort inline compensation.
    ///
    /// <para>
    /// <b>Never relied upon, and the code says so.</b> Every failure here is logged and
    /// swallowed, because the session stays live and the expiry sweeper — layer 2 — will release
    /// everything unconditionally within twenty minutes regardless. Throwing from a compensation
    /// would replace a recoverable situation with an unhandled one.
    /// </para>
    ///
    /// <para>
    /// It exists because twenty minutes of held stock is worth avoiding when we can, not because
    /// it is load-bearing. The distinction matters: a system whose correctness depends on inline
    /// compensation is a system that breaks when the compensation fails for the same reason the
    /// original step did — a database that is down for the write is down for the undo.
    /// </para>
    /// </summary>
    private async Task CompensateAsync(
        CheckoutSession session,
        string reference,
        string why,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Compensating checkout {SessionId} inline because {Why}. The expiry sweeper is the "
            + "guarantee if any of this fails.",
            session.Id,
            why);

        await TryAsync(
            () => dispatcher.SendAsync(
                new RestoreStockCommand(
                    reference,
                    RestoreReason.SessionExpired,
                    session.Lines.Select(line => new StockLine(line.ProductId, line.Quantity)).ToList()),
                cancellationToken),
            "restore stock");

        if (session.PromotionId is not null)
        {
            await TryAsync(
                () => dispatcher.SendAsync(
                    new ReleasePromotionCommand(reference, ReleaseReason.SessionExpired),
                    cancellationToken),
                "release the promotion");
        }

        if (session.PointsRedeemed > 0)
        {
            await TryAsync(
                () => dispatcher.SendAsync(
                    new ReleasePointsCommand(reference, PointsReleaseReason.SessionExpired),
                    cancellationToken),
                "return the points");
        }

        async Task TryAsync(Func<Task> step, string what)
        {
            try
            {
                await step();
            }
            catch (Exception exception)
            {
                // Logged with full context, never swallowed silently. Onyx A16's empty catch
                // blocks in the payment retry loop destroyed all diagnostic information about
                // every gateway failure.
                logger.LogError(
                    exception,
                    "Inline compensation could not {What} for checkout {SessionId}. Leaving it "
                    + "to the expiry sweeper.",
                    what,
                    session.Id);
            }
        }
    }

    private static ConfirmedCheckoutResponse Describe(Order order) =>
        new(
            order.Id,
            order.Number.Value,
            order.Status.ToString(),
            order.Total.Amount,
            order.Total.CurrencyCode);
}
