using BoomboCard.Catalog.Domain.StockMovements;
using BoomboCard.SharedKernel;

namespace BoomboCard.Catalog.Domain.ProductAggregate.Events;

/*
 * Every state change on Product raises one of these, including the ones nothing currently
 * subscribes to. The event is the record of the fact, and a listener added later — a read
 * model, an outbox translator, an audit trail, a price-change notification — must not require
 * the aggregate to be edited before it starts firing.
 *
 * All of them carry ids and primitives only. dome-gym's GymAddedEvent(Subscription, Gym) and
 * RiverBooks' OrderCreatedEvent(Order) pass live aggregate instances, which cannot be
 * serialised into an outbox and let a handler observe state that moved on after the fact was
 * recorded.
 *
 * Records, so they are immutable: a fact does not change after it happened.
 */

public sealed record ProductCreatedEvent(
    Guid ProductId,
    string Sku,
    string Name,
    int InitialQuantity,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record ProductRenamedEvent(
    Guid ProductId,
    string PreviousName,
    string NewName,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;


/// <summary>
/// Carries both the superseded and the new price id, so a consumer can decide whether to
/// honour a quote built on the old price or re-quote — rather than cancelling blindly, which
/// is what Talabat's OnProductPriceChangedCancelSessionsHandler does to every active session
/// touching the product, ejecting customers mid-payment with no explanation.
/// </summary>
public sealed record ProductPriceChangedEvent(
    Guid ProductId,
    string CountryCode,
    Guid? PreviousPriceId,
    decimal? PreviousAmount,
    Guid NewPriceId,
    decimal NewAmount,
    string CurrencyCode,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record ProductPriceWithdrawnEvent(
    Guid ProductId,
    string CountryCode,
    Guid WithdrawnPriceId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record ProductCostPriceChangedEvent(
    Guid ProductId,
    decimal? PreviousAmount,
    decimal NewAmount,
    string CurrencyCode,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record ProductActivatedEvent(
    Guid ProductId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record ProductDiscontinuedEvent(
    Guid ProductId,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record StockDeductedEvent(
    Guid ProductId,
    int Quantity,
    StockReason Reason,
    string Reference,
    int QuantityAfter,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

public sealed record StockRestoredEvent(
    Guid ProductId,
    int Quantity,
    StockReason Reason,
    string Reference,
    int QuantityAfter,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// A stocktake replaced the recorded quantity with a counted one. <paramref name="Delta"/> is
/// the discrepancy that was found — the number worth investigating, and zero when the count
/// matched.
/// </summary>
public sealed record StockAdjustedEvent(
    Guid ProductId,
    int Delta,
    string Reference,
    int QuantityAfter,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Stock written off: loss, breakage or theft. Distinct from a stocktake adjustment because the
/// cause is known, and distinct from a sale because no money came in.
/// </summary>
public sealed record StockShrinkageRecordedEvent(
    Guid ProductId,
    int Quantity,
    string Reference,
    int QuantityAfter,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;

/// <summary>
/// Raised when a deduction takes stock from at-or-above the threshold to below it, so it fires
/// on the crossing rather than on every subsequent sale.
///
/// This replaces Onyx's StockReconciliationJob, which found anything under 5 and silently
/// added 50 units from nowhere every 15 minutes — no supplier, no purchase order, no delivery
/// (D5). Worse, it concealed D1: an oversell drove stock low, the job topped it up, and the
/// discrepancy vanished before anyone could see it. Replenishment is a purchasing decision, so
/// this notifies a human instead of inventing inventory.
/// </summary>
public sealed record StockFellBelowThresholdEvent(
    Guid ProductId,
    string Sku,
    int QuantityAfter,
    int Threshold,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
