using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Payments.Contracts;

/// <summary>
/// Marks a request as a Payments command: it mutates Payments state, and so must run inside a
/// transaction on Payments' context.
///
/// <para>
/// <b>Note which commands in this module deliberately do NOT carry this marker.</b>
/// <c>SubmitPaymentCommand</c> and <c>RefundPaymentCommand</c> call a payment provider over HTTP,
/// and a transaction must never be held across that call — Onyx <b>A12</b> held one across up to
/// three gateway calls with a 100-second timeout each, which on a single-writer database blocked
/// every write in the system for up to five minutes per request. Those two are plain
/// <c>IRequest</c>s that orchestrate: read, call out, then dispatch a marked command to record
/// the outcome in its own short transaction.
/// </para>
///
/// <para>
/// <c>ModuleWiringTests</c> asserts that every <see cref="ITransactionalRequest"/> carries exactly
/// one module marker, which means the absence here is a deliberate, visible choice rather than an
/// oversight that silently loses atomicity.
/// </para>
/// </summary>
public interface IPaymentsRequest : ITransactionalRequest;

/// <summary>Where a payment is, as the rest of the system is allowed to see it.</summary>
public sealed record PaymentSummaryResponse(
    Guid PaymentId,
    string Reference,
    Guid OrderId,
    Guid CustomerId,
    string Status,
    decimal Amount,
    decimal RefundedAmount,
    string CurrencyCode,
    string? DeclineCode,
    DateTimeOffset InitiatedAtUtc,
    DateTimeOffset? CapturedAtUtc);

/// <summary>A query, so no transaction.</summary>
public sealed record GetPaymentSummaryQuery(Guid PaymentId)
    : IRequest<ErrorOr<PaymentSummaryResponse>>;

/// <summary>
/// Whether an order has a live payment, and what became of it. Published so Sales can answer
/// "can this order be cancelled?" without reaching into Payments' tables.
/// </summary>
public sealed record OrderPaymentOutcome(
    Guid OrderId,
    bool Exists,
    bool IsCaptured,
    bool IsLive,
    string? Status);

public sealed record GetOrderPaymentOutcomeQuery(Guid OrderId)
    : IRequest<ErrorOr<OrderPaymentOutcome>>;
