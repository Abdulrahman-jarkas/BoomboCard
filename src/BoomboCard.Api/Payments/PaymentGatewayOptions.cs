using System.ComponentModel.DataAnnotations;

namespace BoomboCard.Api.Payments;

/// <summary>
/// Everything the host needs to reach a payment provider. <b>Validated at startup, so a missing
/// value fails the deployment rather than the first payment.</b>
///
/// <para>
/// <b>Onyx A15:</b> <c>PaymentGatewayOptions.BaseUrl</c> was bound from configuration and then
/// ignored — <c>ChargeAsync</c> and <c>GetGatewayStatusAsync</c> both hardcoded
/// <c>https://payments.onyx-gateway.test/...</c>. The provider could not be pointed at a sandbox
/// without a code change, and the configuration section was decoration.
/// </para>
///
/// <para>
/// <b>Onyx S17 is worse and in the same file:</b> <c>ApiKey</c> was bound and <b>never injected
/// into the client at all</b>. <c>ChargeAsync</c> sent no credential. Even against a reachable
/// provider, every charge would have been rejected as unauthenticated — and because the result
/// was never inspected (<b>M1</b>), every one of those rejections was recorded as a successful
/// payment.
/// </para>
///
/// <para>
/// The two findings compound into something neither says alone: this system could not have taken
/// a single real payment, and nothing in it would have noticed.
/// </para>
/// </summary>
public sealed class PaymentGatewayOptions
{
    public const string SectionName = "PaymentGateway";

    /// <summary>
    /// Where the provider is. <b>Used</b>, which is the whole of <b>A15</b>.
    ///
    /// <para>
    /// <c>[Url]</c> plus the explicit HTTPS check below: a validated URL that happens to be
    /// <c>http://</c> would send an API key and a card token in clear text.
    /// </para>
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// The credential. <b>Attached by a delegating handler</b> — see
    /// <c>GatewayCredentialHandler</c> — rather than by each call site, so there is no call site
    /// that can forget it. <b>S17.</b>
    ///
    /// <para>
    /// Required with no default, and <c>appsettings.json</c> carries no value, so a deployment
    /// without one fails to start. Onyx <b>S5</b> committed a live payment gateway key to
    /// <c>appsettings.json</c> because "it has the real values in it so it works out of the box".
    /// </para>
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(16)]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// The shared secret webhook signatures are verified against. Required for the same reason
    /// the API key is: an unverified webhook is an unauthenticated instruction to change a
    /// payment's status.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(16)]
    public string WebhookSecret { get; init; } = string.Empty;

    /// <summary>
    /// Per-attempt timeout.
    ///
    /// <para>
    /// Thirty seconds, not the <c>HttpClient</c> default of 100. Onyx <b>A12</b> is what the
    /// default costs: three attempts at 100 seconds inside a held transaction was up to five
    /// minutes of write unavailability. The transaction is gone now, but a customer waiting five
    /// minutes for a page is still a failure — and a timeout here is recoverable, because the
    /// idempotency key makes the outcome retrievable.
    /// </para>
    /// </summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// How many times a <b>transient transport failure</b> is retried. Declines are never
    /// retried, whatever this says.
    /// </summary>
    [Range(0, 5)]
    public int TransientRetries { get; init; } = 2;

    /// <summary>
    /// How long a pooled connection is kept before being recycled, so DNS changes are picked up.
    ///
    /// <para>
    /// Two minutes, the <c>IHttpClientFactory</c> default, stated explicitly because the figure
    /// is a real trade-off: shorter means more handshakes, longer means a failed-over provider
    /// endpoint is reached late.
    /// </para>
    /// </summary>
    [Range(1, 60)]
    public int HandlerLifetimeMinutes { get; init; } = 2;

    /// <summary>
    /// Development only. When true, the stub gateway is registered instead of the HTTP one, so
    /// the whole flow can be driven without a provider account.
    ///
    /// <para>
    /// <c>SecurityComposition</c>-style guard: <c>Validate</c> refuses this outside Development,
    /// because a stub gateway in production would approve every payment — which is Onyx
    /// <b>M1</b>'s observable behaviour, reached deliberately.
    /// </para>
    /// </summary>
    public bool UseStub { get; init; }

    /// <summary>
    /// Checks what data annotations cannot.
    ///
    /// <para>
    /// Called from <c>ValidateOnStart</c>, so the process refuses to start rather than taking a
    /// payment over plain HTTP or approving everything with a stub.
    /// </para>
    /// </summary>
    public void Validate(bool isDevelopment)
    {
        if (UseStub && !isDevelopment)
        {
            throw new InvalidOperationException(
                "PaymentGateway:UseStub is set outside Development. The stub approves every "
                + "payment, which is exactly the behaviour Onyx M1 produced by accident. It is "
                + "available in Development only.");
        }

        if (UseStub)
        {
            return;
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"PaymentGateway:BaseUrl ('{BaseUrl}') is not an absolute URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            // A card token and an API key over plain HTTP is S8's consequence without needing to
            // disable certificate validation at all.
            throw new InvalidOperationException(
                "PaymentGateway:BaseUrl must be https. An API key and a card token must never "
                + "cross a network in clear text.");
        }
    }
}
