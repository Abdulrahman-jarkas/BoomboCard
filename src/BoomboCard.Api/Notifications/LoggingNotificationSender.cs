using System.Diagnostics;
using BoomboCard.Notifications.Domain.NotificationAggregate;
using BoomboCard.Notifications.Domain.Sending;

namespace BoomboCard.Api.Notifications;

/// <summary>
/// Writes notifications to the log instead of sending them. <b>Development only.</b>
///
/// <para>
/// <b>The roadmap asks for this specifically, and the reason is Onyx A32.</b> That finding is
/// that <c>CsvImportExportService.ImportProductsAsync</c> was implemented, tested, and advertised
/// in both the README and the PR description — and <i>no endpoint, command or job called it</i>.
/// The only caller was the test project. The obligation line says: "every capability has a
/// reachable entry point and an integration test that exercises it through that entry point."
/// </para>
///
/// <para>
/// A notification path that only works once somebody has a provider account is the same shape: a
/// capability that exists in the code and not in the system, and one nobody exercises end to end
/// until a customer does. With this sender the whole chain — event, consumer, enqueue, claim,
/// send, delivery record — runs on a fresh clone with no setup, which is the only way the chain is
/// routinely tested rather than inspected.
/// </para>
///
/// <para>
/// <b>It redacts a body that contains a token, and that is not optional.</b> The thing that makes
/// this sender useful — putting messages where a developer can read them — is exactly what would
/// put live password-reset links into a log file, and log files are aggregated, shipped off-box
/// and retained far longer than an hour. So the body is replaced, the link is not written, and
/// <c>TheLoggingSenderNeverWritesASecretBody</c> asserts it.
/// </para>
///
/// <para>
/// A developer who needs the actual link gets it from the notification row for the hour before
/// the retention sweep discards it — a deliberate, auditable, time-boxed route rather than a
/// permanent copy in a log aggregator.
/// </para>
/// </summary>
internal sealed class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger)
    : INotificationSender
{
    /// <summary>
    /// Recorded on every attempt row, so a query can answer "did these actually leave the
    /// building?" — which is how a development default reaching production gets noticed.
    /// </summary>
    public string Name => "logging";

    public Task<SendResult> SendAsync(
        NotificationEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();

        var body = envelope.ContainsSecret
            ? $"[redacted: this {envelope.Kind} body contains a single-use token. Read it from "
              + $"GET /api/notifications/{envelope.NotificationId} within the retention window, "
              + "or from the database.]"
            : envelope.Body;

        // Structured, and at Information: in development these ARE the emails, so burying them at
        // Debug would mean the capability is reachable and invisible.
        logger.LogInformation(
            "NOTIFICATION [{Kind}] to {Recipient}\n  Subject: {Subject}\n{Body}\n  "
            + "(notification {NotificationId}, correlation {CorrelationId})",
            envelope.Kind,
            envelope.Recipient,
            envelope.Subject,
            body,
            envelope.NotificationId,
            correlationId);

        stopwatch.Stop();

        // A provider message id, because the delivery record should look the same shape whichever
        // sender handled it — and prefixed so nobody mistakes one for a real provider's.
        return Task.FromResult(new SendResult(
            DeliveryOutcome.Accepted,
            $"log_{correlationId[..12]}",
            "written to the application log; not transmitted",
            (int)stopwatch.ElapsedMilliseconds,
            correlationId,
            Name));
    }
}
