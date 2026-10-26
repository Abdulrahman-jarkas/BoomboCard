using BoomboCard.Sales.Contracts;
using BoomboCard.Sales.Data;
using BoomboCard.Sales.Domain.CheckoutSessionAggregate;
using BoomboCard.Sales.Domain.OrderAggregate;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Sales.Application.Orders.Queries;

/*
 * Queries. None implement ISalesRequest, so no transaction. Every one projects into an explicit
 * response record in SQL — no aggregate is reachable from any of them.
 */

public sealed record QuoteLineResponse(
    Guid ProductId,
    string Sku,
    string Name,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    Guid PriceId);

public sealed record AdjustmentResponse(
    string Kind,
    string Description,
    decimal Amount,
    decimal BasisAmount);

/// <summary>
/// The quote, with every step of the pipeline shown.
///
/// <para>
/// <b>The adjustment rows are the point.</b> Onyx stored one <c>decimal Total</c> computed by a
/// single expression with four interleaved rules, so nothing could say which rule produced which
/// part of it. Here the response carries the lines, the adjustments, and each adjustment's basis
/// — so "why is my tax $6.14?" is answerable from the response itself, and the total is
/// reconstructible by adding the rows up.
/// </para>
/// </summary>
public sealed record CheckoutQuoteResponse(
    Guid CheckoutSessionId,
    string Status,
    string CountryCode,
    string CurrencyCode,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<QuoteLineResponse> Lines,
    IReadOnlyList<AdjustmentResponse> Adjustments,
    decimal MerchandiseSubtotal,
    decimal DiscountedMerchandise,
    decimal Shipping,
    decimal TaxableBase,
    decimal Tax,
    decimal Total);

public sealed record GetCheckoutQuoteQuery(Guid CheckoutSessionId)
    : IRequest<ErrorOr<CheckoutQuoteResponse>>;

public sealed record OrderItemResponse(
    Guid OrderItemId,
    Guid ProductId,
    string Sku,
    string Name,
    int Quantity,
    int QuantityShipped,
    decimal UnitPrice,
    decimal LineTotal,
    Guid PriceId);

public sealed record OrderDetailResponse(
    Guid OrderId,
    string OrderNumber,
    Guid CustomerId,
    string Status,
    string CountryCode,
    string CurrencyCode,
    IReadOnlyList<OrderItemResponse> Items,
    IReadOnlyList<AdjustmentResponse> Adjustments,
    decimal MerchandiseSubtotal,
    decimal Shipping,
    decimal Tax,
    decimal Total,
    DateTimeOffset PlacedAtUtc,
    DateTimeOffset? PaidAtUtc,
    DateTimeOffset? ShippedAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? CancelledAtUtc);

public sealed record GetOrderQuery(Guid OrderId) : IRequest<ErrorOr<OrderDetailResponse>>;

public sealed record OrderSummaryListItem(
    Guid OrderId,
    string OrderNumber,
    string Status,
    decimal Total,
    string CurrencyCode,
    int ItemCount,
    DateTimeOffset PlacedAtUtc);

/// <summary>
/// A page of a customer's orders, newest first, keyset paged over the placed instant.
///
/// Onyx <b>A27</b>: <c>GET /api/orders/search</c> did <c>.Skip(skip).Take(take)</c> with no
/// <c>ORDER BY</c> at all, and <b>A28</b> left <c>take</c> unvalidated.
/// </summary>
public sealed record OrderPage(IReadOnlyList<OrderSummaryListItem> Items, string? NextCursor);

public sealed record GetCustomerOrdersQuery(
    Guid CustomerId,
    string? Cursor = null,
    int Limit = 20) : IRequest<ErrorOr<OrderPage>>;

internal sealed class GetCheckoutQuoteQueryHandler(SalesDbContext context)
    : IRequestHandler<GetCheckoutQuoteQuery, ErrorOr<CheckoutQuoteResponse>>
{
    public async Task<ErrorOr<CheckoutQuoteResponse>> Handle(
        GetCheckoutQuoteQuery query,
        CancellationToken cancellationToken)
    {
        var quote = await context.CheckoutSessions
            .AsNoTracking()
            .Where(session => session.Id == query.CheckoutSessionId)
            .Select(session => new CheckoutQuoteResponse(
                session.Id,
                session.Status.Value.ToString(),
                session.CountryCode,
                session.CurrencyCode,
                session.ExpiresAtUtc,
                session.Lines
                    .OrderBy(line => line.Sku)
                    .Select(line => new QuoteLineResponse(
                        line.ProductId,
                        line.Sku,
                        line.Name,
                        line.Quantity,
                        line.UnitPrice.Amount,
                        line.LineTotal.Amount,
                        line.PriceId))
                    .ToList(),
                session.Adjustments
                    .OrderBy(adjustment => adjustment.Kind)
                    .Select(adjustment => new AdjustmentResponse(
                        adjustment.Kind.ToString(),
                        adjustment.Description,
                        adjustment.Amount,
                        adjustment.BasisAmount))
                    .ToList(),
                session.MerchandiseSubtotal.Amount,
                session.DiscountedMerchandise.Amount,
                session.Shipping.Amount,
                session.TaxableBase.Amount,
                session.Tax.Amount,
                session.Total.Amount))
            .FirstOrDefaultAsync(cancellationToken);

        return quote is null ? CheckoutErrors.NotFound(query.CheckoutSessionId) : quote;
    }
}

internal sealed class GetOrderQueryHandler(SalesDbContext context)
    : IRequestHandler<GetOrderQuery, ErrorOr<OrderDetailResponse>>
{
    public async Task<ErrorOr<OrderDetailResponse>> Handle(
        GetOrderQuery query,
        CancellationToken cancellationToken)
    {
        var order = await context.Orders
            .AsNoTracking()
            .Where(candidate => candidate.Id == query.OrderId)
            .Select(candidate => new OrderDetailResponse(
                candidate.Id,
                candidate.Number.Value,
                candidate.CustomerId,
                candidate.Status.Value.ToString(),
                candidate.CountryCode,
                candidate.Total.CurrencyCode,
                candidate.Items
                    .OrderBy(item => item.Sku)
                    .Select(item => new OrderItemResponse(
                        item.Id,
                        item.ProductId,
                        item.Sku,
                        item.Name,
                        item.Quantity,
                        item.QuantityShipped,
                        item.UnitPrice.Amount,
                        item.LineTotal.Amount,
                        item.PriceId))
                    .ToList(),
                candidate.Adjustments
                    .OrderBy(adjustment => adjustment.Kind)
                    .Select(adjustment => new AdjustmentResponse(
                        adjustment.Kind.ToString(),
                        adjustment.Description,
                        adjustment.Amount,
                        adjustment.BasisAmount))
                    .ToList(),
                candidate.MerchandiseSubtotal.Amount,
                candidate.Shipping.Amount,
                candidate.Tax.Amount,
                candidate.Total.Amount,
                candidate.PlacedAtUtc,
                candidate.PaidAtUtc,
                candidate.ShippedAtUtc,
                candidate.DeliveredAtUtc,
                candidate.CancelledAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

        return order is null ? OrderErrors.NotFound(query.OrderId) : order;
    }
}

internal sealed class GetOrderSummaryQueryHandler(SalesDbContext context)
    : IRequestHandler<GetOrderSummaryQuery, ErrorOr<OrderSummaryResponse>>
{
    public async Task<ErrorOr<OrderSummaryResponse>> Handle(
        GetOrderSummaryQuery query,
        CancellationToken cancellationToken)
    {
        var order = await context.Orders
            .AsNoTracking()
            .Where(candidate => candidate.Id == query.OrderId)
            .Select(candidate => new OrderSummaryResponse(
                candidate.Id,
                candidate.Number.Value,
                candidate.CustomerId,
                candidate.Status.Value.ToString(),
                candidate.Total.Amount,
                candidate.Total.CurrencyCode,
                candidate.PlacedAtUtc,
                candidate.CheckoutSessionId))
            .FirstOrDefaultAsync(cancellationToken);

        return order is null ? OrderErrors.NotFound(query.OrderId) : order;
    }
}

internal sealed class GetCustomerOrdersQueryHandler(SalesDbContext context)
    : IRequestHandler<GetCustomerOrdersQuery, ErrorOr<OrderPage>>
{
    private const int MaxLimit = 100;

    public async Task<ErrorOr<OrderPage>> Handle(
        GetCustomerOrdersQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            return Error.Validation(
                "GetCustomerOrders.InvalidLimit",
                $"Limit must be between 1 and {MaxLimit}.");
        }

        var orders = context.Orders
            .AsNoTracking()
            .Where(order => order.CustomerId == query.CustomerId);

        if (!string.IsNullOrWhiteSpace(query.Cursor)
            && DateTimeOffset.TryParse(
                query.Cursor,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var cursor))
        {
            orders = orders.Where(order => order.PlacedAtUtc < cursor);
        }

        var page = await orders
            .OrderByDescending(order => order.PlacedAtUtc)
            .Take(query.Limit + 1)
            .Select(order => new OrderSummaryListItem(
                order.Id,
                order.Number.Value,
                order.Status.Value.ToString(),
                order.Total.Amount,
                order.Total.CurrencyCode,
                // Counted by the database. Onyx A26 ran one extra query per row from inside a
                // Select over an already-materialised list.
                order.Items.Count,
                order.PlacedAtUtc))
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > query.Limit;
        var items = hasMore ? page[..query.Limit] : page;

        return new OrderPage(items, hasMore ? items[^1].PlacedAtUtc.ToString("O") : null);
    }
}

/// <summary>
/// The outcome of a set of session references — recovery layer 3's missing half.
/// </summary>
internal sealed class GetCheckoutOutcomesQueryHandler(SalesDbContext context)
    : IRequestHandler<GetCheckoutOutcomesQuery, ErrorOr<List<CheckoutSessionOutcome>>>
{
    public async Task<ErrorOr<List<CheckoutSessionOutcome>>> Handle(
        GetCheckoutOutcomesQuery query,
        CancellationToken cancellationToken)
    {
        var ids = query.References
            .Select(reference => Guid.TryParse(reference, out var id) ? id : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        var found = await context.CheckoutSessions
            .AsNoTracking()
            .Where(session => ids.Contains(session.Id))
            .Select(session => new { session.Id, Status = session.Status.Value })
            .ToListAsync(cancellationToken);

        var byId = found.ToDictionary(row => row.Id);

        return query.References
            .Select(reference =>
            {
                if (!Guid.TryParse(reference, out var id) || !byId.TryGetValue(id, out var row))
                {
                    // No session for this reference. The session commits FIRST, so stock held
                    // against an id that does not exist here should be impossible — which makes
                    // this the case worth alerting on rather than quietly releasing.
                    return new CheckoutSessionOutcome(reference, false, false, false);
                }

                return new CheckoutSessionOutcome(
                    reference,
                    true,
                    row.Status is CheckoutStatusValue.Confirmed,
                    row.Status is CheckoutStatusValue.Open or CheckoutStatusValue.Holding);
            })
            .ToList();
    }
}
