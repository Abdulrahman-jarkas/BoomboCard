using System.Security.Cryptography;
using BoomboCard.Payments.Domain.Gateway;
using BoomboCard.Payments.Domain.PaymentAggregate;

namespace BoomboCard.Api.Payments;

/// <summary>
/// A fake provider for development. <b>Registered only in Development, and
/// <c>PaymentGatewayOptions.Validate</c> throws if it is configured anywhere else.</b>
///
/// <para>
/// A stub gateway in production would approve every payment, which is precisely the observable
/// behaviour Onyx <b>M1</b> produced by accident — so the guard against that is a startup failure
/// rather than a comment. The same reasoning as <c>appsettings.json</c> carrying no JWT key:
/// make the unsafe configuration unable to start.
/// </para>
///
/// <para>
/// <b>It is driven by the token, which is what makes it useful.</b> Approving everything would
/// exercise one of four paths and leave the three interesting ones untested by hand. These
/// tokens reach the branches that matter:
/// </para>
///
/// <list type="table">
/// <item><term><c>tok_decline</c></term><description>a definitive decline with
/// <c>insufficient_funds</c> — never retried</description></item>
/// <item><term><c>tok_expired</c></term><description>a decline with <c>expired_card</c>, to see
/// the customer-facing message change</description></item>
/// <item><term><c>tok_timeout</c></term><description><b>indeterminate</b> — the case Onyx got
/// wrong in both directions. The payment stays retryable, no holds are released, and
/// <c>payments.reconciliation</c> resolves it</description></item>
/// <item><term><c>tok_unreachable</c></term><description>a transport failure — the provider never
/// saw it</description></item>
/// <item><term><c>tok_noref</c></term><description>success with no reference, a protocol
/// violation. The payment must NOT settle (<b>M4</b>)</description></item>
/// <item><term>anything else</term><description>success</description></item>
/// </list>
///
/// <para>
/// It also remembers what it was asked, keyed by idempotency key, so <see cref="EnquireAsync"/>
/// can answer honestly — which is what lets the reconciliation path be exercised end to end:
/// submit with <c>tok_timeout</c>, watch the payment sit unresolved, and watch the job resolve it
/// to captured because the stub "really did" take the money.
/// </para>
/// </summary>
internal sealed class StubPaymentGateway(ILogger<StubPaymentGateway> logger) : IPaymentGateway
{
    /// <summary>
    /// What the stub "really" did, keyed by idempotency key — so an enquiry can return the truth
    /// rather than a guess, exactly as a real provider would.
    /// </summary>
    private readonly Dictionary<string, GatewayResult> _recorded = [];

    private readonly Lock _gate = new();

    public Task<GatewayResult> AuthorizeAsync(
        GatewayAuthorizeRequest request,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");

        logger.LogInformation(
            "STUB GATEWAY: authorizing {Amount} {Currency} with token {Token}, idempotency key "
            + "{IdempotencyKey}.",
            request.Amount,
            request.CurrencyCode,
            request.Instrument.Token,
            request.IdempotencyKey);

        // A real provider deduplicates on the key. So does this, which is what makes the
        // mechanism observable in development rather than merely asserted.
        lock (_gate)
        {
            if (_recorded.TryGetValue(request.IdempotencyKey, out var already))
            {
                logger.LogInformation(
                    "STUB GATEWAY: idempotency key {IdempotencyKey} has been seen before; "
                    + "returning the original outcome rather than charging again.",
                    request.IdempotencyKey);

                return Task.FromResult(already);
            }
        }

        var result = request.Instrument.Token switch
        {
            "tok_decline" => new GatewayResult(
                AttemptOutcome.Declined, null, "insufficient_funds",
                "stub: declined for insufficient funds", 40, correlationId),

            "tok_expired" => new GatewayResult(
                AttemptOutcome.Declined, null, "expired_card",
                "stub: the card has expired", 35, correlationId),

            // The outcome is unknown to the CALLER, but the stub records a success — because
            // that is the dangerous real-world shape: the charge succeeded and the reply was
            // lost. Enquiring later returns the capture.
            "tok_timeout" => new GatewayResult(
                AttemptOutcome.Indeterminate, null, null,
                "stub: timed out after the charge was accepted", 30_000, correlationId),

            "tok_unreachable" => new GatewayResult(
                AttemptOutcome.TransportFailure, null, null,
                "stub: connection refused", 15, correlationId),

            "tok_noref" => new GatewayResult(
                AttemptOutcome.Indeterminate, null, null,
                "stub: provider returned success with no reference", 45, correlationId),

            _ => new GatewayResult(
                AttemptOutcome.Succeeded, NewReference("ch"), null,
                "stub: approved", 50, correlationId)
        };

        lock (_gate)
        {
            // What the stub "really" did. For tok_timeout that is a successful charge the caller
            // never learned about — the whole point of the scenario.
            _recorded[request.IdempotencyKey] = request.Instrument.Token is "tok_timeout"
                ? result with { Outcome = AttemptOutcome.Succeeded, ProviderReference = NewReference("ch") }
                : result;
        }

        return Task.FromResult(result);
    }

    public Task<GatewayResult> CaptureAsync(
        GatewayCaptureRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GatewayResult(
            AttemptOutcome.Succeeded,
            request.ProviderReference,
            null,
            "stub: captured",
            20,
            Guid.NewGuid().ToString("N")));

    public Task<GatewayResult> RefundAsync(
        GatewayRefundRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "STUB GATEWAY: refunding {Amount} {Currency} against {ProviderReference}.",
            request.Amount,
            request.CurrencyCode,
            request.ProviderReference);

        return Task.FromResult(new GatewayResult(
            AttemptOutcome.Succeeded,
            NewReference("re"),
            null,
            "stub: refunded",
            30,
            Guid.NewGuid().ToString("N")));
    }

    /// <summary>
    /// Answers honestly from what was recorded. A key the stub has never seen gets a
    /// <c>TransportFailure</c>, which is the right answer: the provider has no record, so the
    /// request never landed.
    /// </summary>
    public Task<GatewayResult> EnquireAsync(
        GatewayEnquiryRequest request,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_recorded.TryGetValue(request.IdempotencyKey, out var recorded))
            {
                logger.LogInformation(
                    "STUB GATEWAY: enquiry for {IdempotencyKey} — the charge {Outcome}.",
                    request.IdempotencyKey,
                    recorded.Outcome);

                return Task.FromResult(recorded with
                {
                    CorrelationId = Guid.NewGuid().ToString("N")
                });
            }
        }

        return Task.FromResult(new GatewayResult(
            AttemptOutcome.TransportFailure,
            null,
            null,
            "stub: no record of this idempotency key",
            10,
            Guid.NewGuid().ToString("N")));
    }

    /// <summary>
    /// Crypto-random even in the stub. A sequential fake reference would let a development
    /// habit — "references are predictable" — survive into production thinking.
    /// </summary>
    private static string NewReference(string prefix) =>
        $"{prefix}_stub_{Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant()}";
}
