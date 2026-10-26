using BoomboCard.Notifications.Application.Dispatch;

namespace BoomboCard.Notifications.Application.IntegrationEvents;

/// <summary>
/// Builds the links that go in verification and reset emails.
///
/// <para>
/// <b>A type rather than two string interpolations, for one reason: the base URL must come from
/// configuration and must actually be used.</b> Onyx <b>A15</b> bound
/// <c>PaymentGatewayOptions.BaseUrl</c> from configuration and then hardcoded
/// <c>https://payments.onyx-gateway.test/...</c> in the client anyway, so the setting was
/// decoration. The same mistake here means every customer in every environment receives a link
/// pointing at production — a staging reset link that resets a real account.
/// </para>
///
/// <para>
/// The token is URL-encoded. It is base32 from <c>SecretToken</c> so today it contains nothing
/// that needs escaping, but a token format is the kind of thing that changes, and a link that
/// silently breaks for one customer in thirty is the worst possible version of that bug.
/// </para>
/// </summary>
public sealed class NotificationLinkBuilder(NotificationsOptions options)
{
    public string EmailVerification(string token) => Build("verify-email", token);

    public string PasswordReset(string token) => Build("reset-password", token);

    private string Build(string path, string token)
    {
        var baseUrl = options.SiteBaseUrl.TrimEnd('/');

        // A missing base URL is a configuration error, and the link is the entire content of the
        // message — so say so in the link rather than producing a plausible-looking relative one
        // that a customer would click and get nowhere. The options validator refuses to start
        // without it, so this is the belt to that braces.
        return string.IsNullOrWhiteSpace(baseUrl)
            ? $"[Notifications:SiteBaseUrl is not configured — link unavailable]"
            : $"{baseUrl}/{path}?token={Uri.EscapeDataString(token)}";
    }
}
