using BoomboCard.Payments.Domain.PaymentAggregate;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Payments.Data.Repositories;

/// <summary>
/// Note the absence of <c>SaveChangesAsync</c>. Persisting is the unit of work's job — Onyx
/// <b>D3</b> committed order creation N+2 separate times.
/// </summary>
internal interface IPaymentRepository
{
    void Add(Payment payment);

    void Add(PaymentClaim claim);

    void Add(ProviderWebhook webhook);

    void AddAttempt(PaymentAttempt attempt);

    /// <summary>
    /// Releases an order's claim, so the customer can try a different card. Called only when a
    /// payment reaches a dead end — see <see cref="PaymentClaim"/>.
    /// </summary>
    void Remove(PaymentClaim claim);

    /// <summary>
    /// Loads a payment with its refunds, which the refund arithmetic needs — the cap is summed
    /// from the rows rather than read from the counter.
    /// </summary>
    Task<Payment?> GetAsync(Guid paymentId, CancellationToken cancellationToken = default);

    Task<Payment?> GetByProviderReferenceAsync(
        string providerReference,
        CancellationToken cancellationToken = default);

    Task<Payment?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<PaymentClaim?> GetClaimAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this provider notification has already been recorded. The read in front of
    /// <c>UX_ProviderWebhooks_EventId</c>, which is the actual guarantee.
    /// </summary>
    Task<bool> WebhookExistsAsync(
        string providerEventId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Payments whose real outcome is unknown, oldest first and capped. The query recovery
    /// depends on: a request that timed out may have charged the card, and only the provider
    /// knows.
    /// </summary>
    Task<List<Guid>> GetIndeterminatePaymentIdsAsync(
        DateTimeOffset olderThanUtc,
        int limit,
        CancellationToken cancellationToken = default);
}

internal sealed class PaymentRepository(PaymentsDbContext context) : IPaymentRepository
{
    public void Add(Payment payment) => context.Payments.Add(payment);

    public void Add(PaymentClaim claim) => context.PaymentClaims.Add(claim);

    public void Add(ProviderWebhook webhook) => context.ProviderWebhooks.Add(webhook);

    public void AddAttempt(PaymentAttempt attempt) => context.PaymentAttempts.Add(attempt);

    public void Remove(PaymentClaim claim) => context.PaymentClaims.Remove(claim);

    public Task<Payment?> GetAsync(
        Guid paymentId,
        CancellationToken cancellationToken = default) =>
        context.Payments
            .Include(payment => payment.Refunds)
            .Include(payment => payment.Attempts)
            .FirstOrDefaultAsync(payment => payment.Id == paymentId, cancellationToken);

    public Task<Payment?> GetByProviderReferenceAsync(
        string providerReference,
        CancellationToken cancellationToken = default) =>
        context.Payments
            .Include(payment => payment.Refunds)
            .Include(payment => payment.Attempts)
            .FirstOrDefaultAsync(
                payment => payment.ProviderReference!.Value == providerReference,
                cancellationToken);

    public Task<Payment?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        context.Payments
            .Include(payment => payment.Refunds)
            .Include(payment => payment.Attempts)
            .FirstOrDefaultAsync(
                payment => payment.IdempotencyKey.Value == idempotencyKey,
                cancellationToken);

    public Task<PaymentClaim?> GetClaimAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        context.PaymentClaims.FirstOrDefaultAsync(
            claim => claim.OrderId == orderId, cancellationToken);

    public Task<bool> WebhookExistsAsync(
        string providerEventId,
        CancellationToken cancellationToken = default) =>
        context.ProviderWebhooks.AnyAsync(
            webhook => webhook.ProviderEventId == providerEventId, cancellationToken);

    public Task<List<Guid>> GetIndeterminatePaymentIdsAsync(
        DateTimeOffset olderThanUtc,
        int limit,
        CancellationToken cancellationToken = default) =>
        context.Payments
            .Where(payment =>
                payment.IndeterminateSinceUtc != null
                && payment.IndeterminateSinceUtc <= olderThanUtc)
            // Ordered because it is capped — an unordered Take could starve the same payments
            // pass after pass, and these are payments that may have charged a customer's card.
            // BUGS-AND-LESSONS §8; the EF warning for this now throws.
            .OrderBy(payment => payment.IndeterminateSinceUtc)
            .Take(limit)
            .Select(payment => payment.Id)
            .ToListAsync(cancellationToken);
}
