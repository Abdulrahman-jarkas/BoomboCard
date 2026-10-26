using BoomboCard.Payments.Domain.PaymentAggregate;

namespace BoomboCard.Payments.Domain.Gateway;

/// <summary>
/// What the customer is paying with, as a <b>provider token</b> — never card data.
///
/// <para>
/// The client tokenises the card against the provider directly and sends us the token. Raw card
/// numbers never touch this system, which is the only version of this that does not drag the
/// entire codebase into PCI-DSS scope. There is deliberately no <c>CardNumber</c> property here
/// for anyone to be tempted by.
/// </para>
/// </summary>
public sealed record PaymentInstrument(string Token, string? Last4 = null, string? Brand = null);

/// <summary>
/// What a provider said, in a shape that cannot express "probably fine".
///
/// <para>
/// <b>Onyx's equivalent was <c>class PaymentGatewayResult { bool Success; string GatewayReference; }</c></b>,
/// and that single boolean is the root of two Critical findings. A two-state result cannot
/// represent "we do not know", so the code had to pick one — and it picked both, in different
/// places: the controller read a lost response as success (<b>M1</b>) while the retry loop read
/// it as failure and charged again (<b>M5</b>).
/// </para>
///
/// <para>
/// So this type has four outcomes and the caller must handle each. <c>Indeterminate</c> is the
/// one that earns its keep: a timeout against a payment provider is not a failure, it is an
/// unanswered question, and the only correct response is to go and ask.
/// </para>
/// </summary>
/// <param name="Outcome">Which of the four answers this was.</param>
/// <param name="ProviderReference">
/// Present only when the provider actually gave one. <b>Never synthesised</b> — the whole of
/// <b>M4</b> is that this field stays null when there is no answer.
/// </param>
/// <param name="ProviderCode">
/// The provider's own code: <c>insufficient_funds</c>, <c>expired_card</c>, <c>do_not_honor</c>.
/// What makes a decline actionable for the customer rather than merely final.
/// </param>
/// <param name="Detail">
/// The provider's message, or our exception type and message. Logged and persisted on the attempt
/// row, never shown to a customer.
/// </param>
/// <param name="LatencyMs">How long the call took, for the attempt row and for latency alerting.</param>
/// <param name="CorrelationId">
/// Ours, generated per request and sent as a header, so a log line, an attempt row and the
/// provider's own record of the request all join up. <b>A16</b>: Onyx logged gateway failures to
/// <c>Console.WriteLine</c> with nothing identifying the payment.
/// </param>
public sealed record GatewayResult(
    AttemptOutcome Outcome,
    string? ProviderReference,
    string? ProviderCode,
    string? Detail,
    int LatencyMs,
    string CorrelationId)
{
    public bool Succeeded => Outcome is AttemptOutcome.Succeeded;

    /// <summary>
    /// Whether the provider's answer settles the matter. A decline does; a transport failure and
    /// an indeterminate outcome do not.
    /// </summary>
    public bool IsDefinitive => Outcome is AttemptOutcome.Succeeded or AttemptOutcome.Declined;
}

/// <summary>
/// The payment provider, as this module needs it.
///
/// <para>
/// <b>An interface here and an implementation in the host</b>, exactly as
/// <c>IAccessTokenIssuer</c> is. The reasoning is the same: the base address, the API key, the
/// retry policy and the HTTP stack are all deployment concerns, and the module that decides
/// <i>what</i> a payment means should not also own <i>how</i> to reach a particular vendor. The
/// practical consequence is that <c>BoomboCard.Payments</c> references no HTTP package at all —
/// the same arrangement that keeps it free of a database provider.
/// </para>
///
/// <para>
/// <b>Every method returns a <see cref="GatewayResult"/> and none of them throw for a provider
/// outcome.</b> An exception means a bug in the adapter, not a declined card. Transport failures
/// are caught in the adapter, where the exception type is known, and turned into the right
/// outcome — which is <b>A16</b> inverted: Onyx's <c>catch { }</c> destroyed that information,
/// and here converting it is the adapter's entire job.
/// </para>
///
/// <para>
/// <b>Every call takes the idempotency key.</b> Not optional, not defaulted — a signature that
/// permits a request without one permits <b>M5</b>.
/// </para>
/// </summary>
public interface IPaymentGateway
{
    /// <summary>
    /// Reserves the funds, or takes them, depending on the provider. The adapter reports which
    /// by the outcome it returns; the aggregate handles both.
    /// </summary>
    Task<GatewayResult> AuthorizeAsync(
        GatewayAuthorizeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Takes funds that were previously reserved.</summary>
    Task<GatewayResult> CaptureAsync(
        GatewayCaptureRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Gives money back, in whole or in part.</summary>
    Task<GatewayResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// "What happened to the request carrying this idempotency key?"
    ///
    /// <para>
    /// <b>The method that resolves an indeterminate outcome, and the reason the idempotency key
    /// has to be durable.</b> A timeout leaves the real outcome recorded at the provider and
    /// nowhere else; this is how it is retrieved. Without it the only options are to charge again
    /// (Onyx's choice) or to abandon a payment that may have succeeded.
    /// </para>
    ///
    /// <para>
    /// A read, so it is safe to repeat and safe to run from a background job.
    /// </para>
    /// </summary>
    Task<GatewayResult> EnquireAsync(
        GatewayEnquiryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A charge request.
///
/// <para>
/// <c>IdempotencyKey</c> is stable for the life of the payment and sent on every attempt
/// (<b>M5</b>). <c>Amount</c> is derived server-side from the order — the client never supplies
/// it (<b>M2</b>).
/// </para>
/// </summary>
public sealed record GatewayAuthorizeRequest(
    Guid PaymentId,
    string IdempotencyKey,
    decimal Amount,
    string CurrencyCode,
    PaymentInstrument Instrument,
    string Description);

public sealed record GatewayCaptureRequest(
    Guid PaymentId,
    string IdempotencyKey,
    string ProviderReference,
    decimal Amount,
    string CurrencyCode);

/// <summary>
/// A refund request.
///
/// <para>
/// <c>IdempotencyKey</c> is the <b>refund's</b> own key, not the payment's. Two refunds against
/// one payment are two distinct provider transactions and must deduplicate independently.
/// </para>
/// </summary>
public sealed record GatewayRefundRequest(
    Guid PaymentId,
    string IdempotencyKey,
    string ProviderReference,
    decimal Amount,
    string CurrencyCode,
    string Reason);

public sealed record GatewayEnquiryRequest(Guid PaymentId, string IdempotencyKey);
