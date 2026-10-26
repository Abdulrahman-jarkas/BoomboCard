using BoomboCard.Sales.Contracts;
using BoomboCard.Sales.Data;
using BoomboCard.Sales.Data.Repositories;
using BoomboCard.Sales.Domain.OrderAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Sales.Application.Orders.Commands;

/// <summary>
/// Records a provider's confirmed capture against an order.
///
/// <para>
/// Idempotent on the payment reference — a redelivered capture marks the order paid once. That
/// matters more here than anywhere: Onyx <b>M3</b> is "no duplicate-payment protection" and
/// <b>M5</b> is "retries can double-charge", and this is the Sales-side half of not repeating
/// either.
/// </para>
/// </summary>
internal sealed class MarkOrderPaidCommandHandler(
    ISalesRepository sales,
    IUnitOfWork<SalesDbContext> unitOfWork,
    TimeProvider timeProvider,
    ILogger<MarkOrderPaidCommandHandler> logger)
    : IRequestHandler<MarkOrderPaidCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        MarkOrderPaidCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.PaymentReference))
        {
            return OrderErrors.PaymentReferenceRequired;
        }

        var alreadyApplied = await sales.GetOrderByPaymentReferenceAsync(
            command.PaymentReference, cancellationToken);

        if (alreadyApplied is not null)
        {
            return Result.Success;
        }

        var order = await sales.GetOrderAsync(command.OrderId, cancellationToken);

        if (order is null)
        {
            return OrderErrors.NotFound(command.OrderId);
        }

        var paid = order.MarkPaid(command.PaymentReference, timeProvider.GetUtcNow());

        if (paid.IsError)
        {
            return paid.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Order {OrderNumber} marked paid against provider reference {PaymentReference}.",
            order.Number.Value,
            command.PaymentReference);

        return Result.Success;
    }
}

/// <summary>
/// Cancels an order. The <c>OrderCancelledEvent</c> it raises is what puts the stock, the
/// promotion use and the points back — through the outbox, after this transaction commits.
/// </summary>
internal sealed class CancelOrderCommandHandler(
    ISalesRepository sales,
    IUnitOfWork<SalesDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<CancelOrderCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        CancelOrderCommand command,
        CancellationToken cancellationToken)
    {
        var order = await sales.GetOrderAsync(command.OrderId, cancellationToken);

        if (order is null)
        {
            return OrderErrors.NotFound(command.OrderId);
        }

        var cancelled = order.Cancel(
            string.IsNullOrWhiteSpace(command.Reason) ? "cancelled" : command.Reason,
            timeProvider.GetUtcNow());

        if (cancelled.IsError)
        {
            return cancelled.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// Records a refund. The event it raises reverses the loyalty points the order earned.
/// </summary>
internal sealed class RefundOrderCommandHandler(
    ISalesRepository sales,
    IUnitOfWork<SalesDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<RefundOrderCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RefundOrderCommand command,
        CancellationToken cancellationToken)
    {
        var order = await sales.GetOrderAsync(command.OrderId, cancellationToken);

        if (order is null)
        {
            return OrderErrors.NotFound(command.OrderId);
        }

        var refunded = order.MarkRefunded(command.RefundReference, timeProvider.GetUtcNow());

        if (refunded.IsError)
        {
            return refunded.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>Ships some or all of an order's items. Supports partial shipment.</summary>
public sealed record ShipOrderCommand(
    Guid OrderId,
    IReadOnlyList<ShipmentLine> Lines) : IRequest<ErrorOr<Success>>, ISalesRequest;

public sealed record ShipmentLine(Guid OrderItemId, int Quantity);

internal sealed class ShipOrderCommandHandler(
    ISalesRepository sales,
    IUnitOfWork<SalesDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<ShipOrderCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ShipOrderCommand command,
        CancellationToken cancellationToken)
    {
        var order = await sales.GetOrderAsync(command.OrderId, cancellationToken);

        if (order is null)
        {
            return OrderErrors.NotFound(command.OrderId);
        }

        var shipped = order.Ship(
            command.Lines.Select(line => (line.OrderItemId, line.Quantity)).ToList(),
            timeProvider.GetUtcNow());

        if (shipped.IsError)
        {
            return shipped.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>Marks a fully shipped order delivered.</summary>
public sealed record MarkOrderDeliveredCommand(Guid OrderId)
    : IRequest<ErrorOr<Success>>, ISalesRequest;

internal sealed class MarkOrderDeliveredCommandHandler(
    ISalesRepository sales,
    IUnitOfWork<SalesDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<MarkOrderDeliveredCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        MarkOrderDeliveredCommand command,
        CancellationToken cancellationToken)
    {
        var order = await sales.GetOrderAsync(command.OrderId, cancellationToken);

        if (order is null)
        {
            return OrderErrors.NotFound(command.OrderId);
        }

        var delivered = order.MarkDelivered(timeProvider.GetUtcNow());

        if (delivered.IsError)
        {
            return delivered.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>Cancels a checkout before it is confirmed.</summary>
public sealed record CancelCheckoutCommand(Guid CheckoutSessionId)
    : IRequest<ErrorOr<Success>>, ISalesRequest;

internal sealed class CancelCheckoutCommandHandler(
    ISalesRepository sales,
    IUnitOfWork<SalesDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<CancelCheckoutCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        CancelCheckoutCommand command,
        CancellationToken cancellationToken)
    {
        var session = await sales.GetSessionAsync(command.CheckoutSessionId, cancellationToken);

        if (session is null)
        {
            return Domain.CheckoutSessionAggregate.CheckoutErrors.NotFound(command.CheckoutSessionId);
        }

        var cancelled = session.Cancel("cancelled by the customer", timeProvider.GetUtcNow());

        if (cancelled.IsError)
        {
            return cancelled.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}
