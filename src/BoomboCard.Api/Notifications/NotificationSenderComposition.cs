using BoomboCard.Notifications.Domain.Sending;
using Polly;

namespace BoomboCard.Api.Notifications;

/// <summary>
/// Wires the notification sender into the host.
///
/// <para>
/// Everything here is a deployment concern, which is why it lives in the host rather than in
/// <c>BoomboCard.Notifications</c> — the same split as <c>IPaymentGateway</c> and
/// <c>IAccessTokenIssuer</c>. The module declares <see cref="INotificationSender"/> and references
/// no HTTP or SMTP package at all.
/// </para>
/// </summary>
public static class NotificationSenderComposition
{
    public static IServiceCollection AddNotificationSender(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        services.AddOptions<NotificationSenderOptions>()
            .Bind(configuration.GetSection(NotificationSenderOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options =>
                {
                    options.Validate(isDevelopment);
                    return true;
                },
                "NotificationSender configuration is not usable.")
            .ValidateOnStart();

        var options = configuration
            .GetSection(NotificationSenderOptions.SectionName)
            .Get<NotificationSenderOptions>() ?? new NotificationSenderOptions();

        if (options.UseLogging && isDevelopment)
        {
            // The sender the roadmap asks for by name, "so the capability is reachable and tested
            // rather than wired up separately per environment" — which is A32's lesson. Onyx's
            // CSV import was implemented, tested, advertised, and called by nothing but the test
            // project.
            services.AddSingleton<INotificationSender, LoggingNotificationSender>();

            return services;
        }

        services.AddTransient<NotificationCredentialHandler>();

        services
            .AddHttpClient<INotificationSender, HttpNotificationSender>(client =>
            {
                client.BaseAddress = new Uri(
                    options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/");

                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddHttpMessageHandler<NotificationCredentialHandler>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(2))
            // ONE immediate retry here, and no more.
            //
            // The real retry budget lives in the notification itself — five attempts with
            // exponential backoff over roughly eight hours, persisted, visible, and
            // dead-lettered at the end. This one covers a single dropped connection without
            // spending one of those five; anything beyond it would be two competing retry
            // policies, one of which nobody can see.
            //
            // Onyx A14 is the opposite arrangement: a hand-rolled loop of three, constructing a
            // new HttpClient and handler each time, with no persisted record of any of it.
            .AddTransientHttpErrorPolicy(builder => builder.WaitAndRetryAsync(
                1, _ => TimeSpan.FromMilliseconds(200 + Random.Shared.Next(0, 200))));

        // NOTE what is absent, as in PaymentGatewayComposition: any configuration of
        // ServerCertificateCustomValidationCallback or DangerousAcceptAnyServerCertificateValidator.
        // A notification body can carry a single-use password-reset token, so S8's consequence
        // here is an attacker reading reset links off the wire. Banned solution-wide by
        // AT NothingDisablesTlsCertificateValidation.
        return services;
    }
}
