using BoomboCard.SharedKernel;

namespace BoomboCard.Payments.Domain.PaymentAggregate.Events;

/*
 * Payments' internal domain events. Translated to published contracts by the outbox translators,
 * which run inside the same transaction as the state change that raised them.
 *
 * Onyx A9 dispatched its payment notification BEFORE the payment row was even added to the
 * context, so a customer could be told their payment had succeeded for a transaction that then
 * rolled back. Every event here is enqueued as an outbox row in the same SaveChanges as the
 * status transition, so there is no state in which one exists without the other.
 */

public sealed record PaymentInitiatedEvent(
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    decimal Amount,
    string CurrencyCode,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record PaymentAuthorizedEvent(
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    decimal Amount,
    string CurrencyCode,
    string ProviderReference,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The money moved. <b>The event Sales has been waiting for since Phase 4</b> —
/// <c>MarkOrderPaidCommand</c> documented itself as a stand-in until this existed.
/// </summary>
public sealed record PaymentCapturedEvent(
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    Guid CheckoutSessionId,
    decimal Amount,
    string CurrencyCode,
    string ProviderReference,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// The provider refused. Carries the checkout session so Sales can release the holds placed
/// under it — the first use of <c>RestoreReason.PaymentFailed</c>, which has existed since
/// Phase 0 with nothing to raise it.
/// </summary>
public sealed record PaymentDeclinedEvent(
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    Guid CheckoutSessionId,
    string? DeclineCode,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// We could not get an answer.
///
/// <para>
/// <paramref name="OutcomeIsUnknown"/> is the field that keeps <b>M1</b> closed downstream:
/// a consumer must not release the order's holds when the charge may have succeeded, because
/// releasing stock for an order that was in fact paid is an oversell. Only an exhausted budget
/// with a <i>known</i> failure is safe to compensate for.
/// </para>
/// </summary>
public sealed record PaymentFailedEvent(
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    Guid CheckoutSessionId,
    bool OutcomeIsUnknown,
    bool AttemptsExhausted,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record PaymentRefundedEvent(
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    decimal Amount,
    string CurrencyCode,
    decimal RefundedTotal,
    bool Fully,
    string ProviderReference,
    string Reason,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record PaymentChargedBackEvent(
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    decimal Amount,
    string CurrencyCode,
    string DisputeReference,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record PaymentCancelledEvent(
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    Guid CheckoutSessionId,
    string Reason,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
