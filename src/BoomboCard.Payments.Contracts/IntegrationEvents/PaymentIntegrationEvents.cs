using BoomboCard.SharedKernel;

namespace BoomboCard.Payments.Contracts.IntegrationEvents;

/*
 * Payments' cross-boundary facts.
 *
 * PaymentCapturedIntegrationEvent is the one the system has been waiting for since Phase 4:
 * Sales' MarkOrderPaidCommand documented itself as a stand-in "because Payments does not exist
 * yet (Phase 5). When it does, the shape becomes Sales subscribing to its
 * PaymentCapturedIntegrationEvent through the inbox and calling this."
 *
 * That is exactly the shape, and the handler is three lines of work.
 */

/// <summary>
/// The money moved. Sales marks the order paid, which in turn is what lets Loyalty earn.
///
/// <para>
/// Note the chain this completes: capture → order paid → points earned. Three modules, no module
/// referencing another's internals, each step guaranteed by an outbox row committed with the
/// state change that produced it. Onyx did all three in one controller action with four separate
/// commits and no compensation.
/// </para>
/// </summary>
public sealed record PaymentCapturedIntegrationEvent(
    Guid EventId,
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    string ProviderReference,
    decimal Amount,
    string CurrencyCode,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// The provider refused. Definitive, so the order can be cancelled and its holds released.
///
/// <para>
/// Carries <paramref name="CheckoutSessionId"/> because that is the reference every hold in every
/// module was placed under. This is the first event in the system that genuinely means
/// <c>RestoreReason.PaymentFailed</c> — a value that has existed in Catalog's contracts since
/// Phase 0 with nothing able to raise it.
/// </para>
/// </summary>
public sealed record PaymentDeclinedIntegrationEvent(
    Guid EventId,
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    Guid CheckoutSessionId,
    string? DeclineCode,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// We could not get an answer out of the provider.
///
/// <para>
/// <b><paramref name="OutcomeIsUnknown"/> is the most important flag in these contracts.</b> A
/// consumer must <i>not</i> release the order's holds while the charge may have succeeded:
/// releasing stock for an order that was in fact paid is an oversell, and an oversell caused by
/// cleaning up is worse than one caused by a race because it looks deliberate.
/// </para>
///
/// <para>
/// So the only safe compensation trigger is <c>AttemptsExhausted &amp;&amp; !OutcomeIsUnknown</c>.
/// Anything else waits for <c>payments.reconciliation</c> to ask the provider what actually
/// happened. Onyx's version of this decision was to treat every unanswered request as a failure
/// and charge the card again (<b>M5</b>).
/// </para>
/// </summary>
public sealed record PaymentFailedIntegrationEvent(
    Guid EventId,
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    Guid CheckoutSessionId,
    bool OutcomeIsUnknown,
    bool AttemptsExhausted,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// Money was given back. Sales records the refund, which reverses the loyalty points the order
/// earned.
/// </summary>
public sealed record PaymentRefundedIntegrationEvent(
    Guid EventId,
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    decimal Amount,
    string CurrencyCode,
    bool Fully,
    string ProviderReference,
    string Reason,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;

/// <summary>
/// The cardholder disputed the charge and the provider pulled the funds.
///
/// <para>
/// Published rather than handled quietly because a chargeback is a business event, not a
/// technical one: fulfilment should stop, the customer's standing is affected, and somebody has
/// to decide whether to defend it. <c>ISSUES.md</c> §7 lists chargeback handling as a capability
/// Onyx did not have, and <b>M4</b> is why it could not have had one.
/// </para>
/// </summary>
public sealed record PaymentChargedBackIntegrationEvent(
    Guid EventId,
    Guid PaymentId,
    Guid OrderId,
    Guid CustomerId,
    decimal Amount,
    string CurrencyCode,
    string DisputeReference,
    DateTimeOffset OccurredAtUtc) : IIntegrationEvent;
