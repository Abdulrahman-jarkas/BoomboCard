using System.Security.Cryptography;
using System.Text;
using BoomboCard.Payments.Domain.Gateway;
using Polly;
using Microsoft.Extensions.Options;

namespace BoomboCard.Api.Payments;

/// <summary>
/// Wires the payment provider into the host.
///
/// <para>
/// Everything here is a deployment concern, which is why it lives in the host rather than in
/// <c>BoomboCard.Payments</c> — the same split as <c>IAccessTokenIssuer</c>. The module declares
/// <see cref="IPaymentGateway"/> and references no HTTP package at all.
/// </para>
/// </summary>
public static class PaymentGatewayComposition
{
    public static IServiceCollection AddPaymentGateway(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        // A15: options bound, validated by annotations, and validated AT STARTUP. Onyx bound its
        // gateway options and then ignored them — the base address was hardcoded and the API key
        // was never injected, so the whole section was decoration.
        services.AddOptions<PaymentGatewayOptions>()
            .Bind(configuration.GetSection(PaymentGatewayOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options =>
                {
                    options.Validate(isDevelopment);
                    return true;
                },
                "PaymentGateway configuration is not usable.")
            .ValidateOnStart();

        var options = configuration
            .GetSection(PaymentGatewayOptions.SectionName)
            .Get<PaymentGatewayOptions>() ?? new PaymentGatewayOptions();

        services.AddSingleton<IPaymentWebhookVerifier, HmacPaymentWebhookVerifier>();

        if (options.UseStub && isDevelopment)
        {
            // Singleton, because the stub remembers what it was asked by idempotency key — which
            // is what lets the reconciliation path be exercised by hand.
            services.AddSingleton<IPaymentGateway, StubPaymentGateway>();

            return services;
        }

        services.AddTransient<GatewayCredentialHandler>();

        // A14: ONE typed client with a POOLED handler, injected.
        //
        // Onyx constructed `new HttpClientHandler` and `new HttpClient(handler)` on every call,
        // inside a retry loop. Each disposal left a socket in TIME_WAIT, so under load the
        // ephemeral port range ran out. Separately it kept a `static HttpClient` whose
        // DefaultRequestHeaders it mutated per call — a race if ever called concurrently.
        services
            .AddHttpClient<IPaymentGateway, HttpPaymentGateway>(client =>
            {
                // From validated options. There is no absolute URL in the adapter — A15.
                client.BaseAddress = new Uri(
                    options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/");

                // 30s, not the 100s default. See PaymentGatewayOptions.TimeoutSeconds.
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            })
            // S17: the credential is attached here, so no call site can forget it.
            .AddHttpMessageHandler<GatewayCredentialHandler>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(options.HandlerLifetimeMinutes))
            // Retry on transient transport failures only, with exponential backoff and jitter.
            //
            // Safe ONLY because every request carries the payment's stable idempotency key —
            // which is the entire basis Onyx's PR claimed and did not have: "the gateway call is
            // idempotent on their end, so the retry loop won't double-charge". Nothing in its
            // request identified the payment.
            //
            // Note what is NOT retried: a decline. It arrives as a 402 or a 200 with a declined
            // body, neither of which this policy matches, because retrying a provider's
            // considered answer is how a bank starts treating you as an attacker.
            .AddTransientHttpErrorPolicy(builder => builder.WaitAndRetryAsync(
                options.TransientRetries,
                attempt => TimeSpan.FromMilliseconds(
                    Math.Pow(2, attempt) * 200 + Random.Shared.Next(0, 200))));

        // NOTE what is deliberately absent from this whole method: any configuration of
        // ServerCertificateCustomValidationCallback, HttpClientHandler, or
        // DangerousAcceptAnyServerCertificateValidator.
        //
        // Onyx S8 is `ServerCertificateCustomValidationCallback = (_, _, _, _) => true` on the
        // payment gateway client — the one control preventing an attacker impersonating the
        // provider, switched off, with card tokens and charge requests on the wire. The
        // obligation line is unambiguous: "never override certificate validation. If a sandbox
        // needs a private CA, trust that CA at the machine level in that environment only."
        //
        // A source-convention test bans the pattern across the entire solution, so this is not
        // merely absent — it is unable to come back.
        return services;
    }
}

/// <summary>
/// Verifies that a webhook came from the provider.
///
/// <para>
/// An interface in the host because the signing secret is a deployment concern, and the module
/// receives only the verdict — <c>SignatureVerified: true/false</c>. A module that could not
/// verify a signature itself is a module that would have to be told to trust its caller, and an
/// unverified webhook is an unauthenticated instruction to mark a payment captured.
/// </para>
/// </summary>
public interface IPaymentWebhookVerifier
{
    bool Verify(string payload, string? signatureHeader);
}

/// <summary>
/// HMAC-SHA256 over the raw body, compared in fixed time.
///
/// <para>
/// <b>Fixed-time comparison matters here specifically.</b> A naive <c>==</c> on the hex digest
/// returns early at the first differing character, which leaks how much of a guess was correct —
/// and unlike a password, an attacker can submit webhook signatures as fast as the network
/// allows. <c>CryptographicOperations.FixedTimeEquals</c> is the same primitive
/// <c>PasswordCredential</c> relies on in Customers.
/// </para>
/// </summary>
internal sealed class HmacPaymentWebhookVerifier(
    IOptions<PaymentGatewayOptions> options,
    ILogger<HmacPaymentWebhookVerifier> logger) : IPaymentWebhookVerifier
{
    public bool Verify(string payload, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            logger.LogWarning("A payment webhook arrived with no signature header.");
            return false;
        }

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.Value.WebhookSecret),
            Encoding.UTF8.GetBytes(payload));

        byte[] provided;

        try
        {
            provided = Convert.FromHexString(signatureHeader.Trim());
        }
        catch (FormatException)
        {
            // Specific, and logged. Not a bare `catch` — Onyx A16.
            logger.LogWarning("A payment webhook signature was not valid hex.");
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
