namespace BoomboCard.Sales.Application.Reconciliation;

public sealed class SalesOptions
{
    /// <summary>How often dead checkout sessions are swept. <b>Recovery layer 2.</b></summary>
    public TimeSpan ExpirySweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often stock holds with no matching sale are hunted down. <b>Recovery layer 3.</b>
    /// Rarer than the expiry sweep on purpose: layer 2 catches almost everything, and this one
    /// exists for the cases layer 2 structurally cannot see.
    /// </summary>
    public TimeSpan OrphanSweepInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a hold must have sat before the orphan sweep will look at it.
    ///
    /// <para>
    /// Must be comfortably longer than <c>CheckoutSession.Lifetime</c> plus the expiry sweep's
    /// interval, or this job would race the checkouts that are legitimately holding stock and
    /// the sessions layer 2 is about to release. An hour against a twenty-minute lifetime leaves
    /// a wide margin; the cost of waiting is stock held slightly longer, and the cost of not
    /// waiting is releasing stock out from under a live customer.
    /// </para>
    /// </summary>
    public TimeSpan OrphanGracePeriod { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often order totals are re-derived from their rows.</summary>
    public TimeSpan IntegrityCheckInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a processed outbox message is kept.</summary>
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromDays(30);
}
