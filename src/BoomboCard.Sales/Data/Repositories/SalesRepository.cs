using BoomboCard.Sales.Domain.CheckoutSessionAggregate;
using BoomboCard.Sales.Domain.Numbering;
using BoomboCard.Sales.Domain.OrderAggregate;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Sales.Data.Repositories;

/// <summary>
/// Note the absence of <c>SaveChangesAsync</c>. Persisting is the unit of work's job — Onyx
/// <b>D3</b> committed order creation N+2 separate times.
/// </summary>
internal interface ISalesRepository
{
    void Add(CheckoutSession session);

    void Add(Order order);

    void Add(OrderNumberSequence sequence);

    Task<CheckoutSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<Order?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<Order?> GetOrderBySessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<Order?> GetOrderByPaymentReferenceAsync(
        string paymentReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The sequence row, loaded tracked so the version token applies. Created on first use —
    /// so there is no seeding step that can be forgotten, and no migration that has to insert
    /// data.
    /// </summary>
    Task<OrderNumberSequence> GetOrCreateSequenceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Live sessions past their deadline, oldest first and capped. The query recovery layer 2
    /// depends on.
    /// </summary>
    Task<List<Guid>> GetExpiredSessionIdsAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The outcome of a set of session references, for the orphan-sale reconciliation. A
    /// reference with no session is reported as not existing, which is the case worth alerting
    /// on — the session commits first, so stock held against an unknown id should be impossible.
    /// </summary>
    Task<List<CheckoutSession>> GetSessionsByIdsAsync(
        IReadOnlyCollection<Guid> sessionIds,
        CancellationToken cancellationToken = default);
}

internal sealed class SalesRepository(SalesDbContext context) : ISalesRepository
{
    public void Add(CheckoutSession session) => context.CheckoutSessions.Add(session);

    public void Add(Order order) => context.Orders.Add(order);

    public void Add(OrderNumberSequence sequence) => context.OrderNumberSequences.Add(sequence);

    public Task<CheckoutSession?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        context.CheckoutSessions
            .Include(session => session.Lines)
            .Include(session => session.Adjustments)
            .FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken);

    public Task<Order?> GetOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        context.Orders
            .Include(order => order.Items)
            .Include(order => order.Adjustments)
            // Payments are included because PaymentReference reads them — a derived property
            // over an unloaded navigation would silently answer null, which on this property
            // would read as "never captured".
            .Include(order => order.Payments)
            .FirstOrDefaultAsync(order => order.Id == orderId, cancellationToken);

    public Task<Order?> GetOrderBySessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        context.Orders
            .Include(order => order.Items)
            .FirstOrDefaultAsync(order => order.CheckoutSessionId == sessionId, cancellationToken);

    public Task<Order?> GetOrderByPaymentReferenceAsync(
        string paymentReference,
        CancellationToken cancellationToken = default) =>
        context.Orders
            // Filters on the payment ROW, which is what UX_OrderPayments_Reference indexes.
            // PaymentReference on Order is a derived C# property and cannot be translated.
            .Where(order => order.Payments.Any(payment => payment.Reference == paymentReference))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<OrderNumberSequence> GetOrCreateSequenceAsync(
        CancellationToken cancellationToken = default)
    {
        var sequence = await context.OrderNumberSequences
            .FirstOrDefaultAsync(row => row.Id == OrderNumberSequence.SingletonId, cancellationToken);

        if (sequence is not null)
        {
            return sequence;
        }

        // First order ever. Two simultaneous first orders both create it and the primary key
        // rejects the second, which the dispatcher retries — so the race resolves itself and
        // there is no seeding step to forget.
        sequence = OrderNumberSequence.Create();
        context.OrderNumberSequences.Add(sequence);

        return sequence;
    }

    public Task<List<Guid>> GetExpiredSessionIdsAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken = default) =>
        context.CheckoutSessions
            .Where(session =>
                (session.Status.Value == CheckoutStatusValue.Open
                    || session.Status.Value == CheckoutStatusValue.Holding)
                && session.ExpiresAtUtc <= nowUtc)
            // Ordered because it is capped — an unordered Take could starve the same sessions
            // pass after pass, and these are sessions holding stock. That bug was live in
            // Catalog's own jobs (BUGS-AND-LESSONS §8) and the EF warning for it now throws.
            .OrderBy(session => session.ExpiresAtUtc)
            .Take(limit)
            .Select(session => session.Id)
            .ToListAsync(cancellationToken);

    public async Task<List<CheckoutSession>> GetSessionsByIdsAsync(
        IReadOnlyCollection<Guid> sessionIds,
        CancellationToken cancellationToken = default) =>
        sessionIds.Count == 0
            ? []
            : await context.CheckoutSessions
                .AsNoTracking()
                .Where(session => sessionIds.Contains(session.Id))
                .ToListAsync(cancellationToken);
}
