using BoomboCard.Loyalty.Domain.Ledger;
using BoomboCard.Loyalty.Domain.PointsAccountAggregate.Events;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Loyalty.Domain.PointsAccountAggregate;

/// <summary>
/// One customer's points balance.
///
/// Invariants it holds:
///  - The balance never goes negative.
///  - Every movement produces a ledger entry; nothing changes the balance silently.
///  - A closed account cannot change.
///  - A redemption is validated before it is a discount, and debited in the transaction that
///    commits the order.
///
/// <para>
/// <b>What this replaces.</b> Onyx's loyalty scheme was one <c>int</c> on <c>Customer</c> and one
/// method:
/// </para>
///
/// <code>
/// if (customer.LoyaltyPoints &lt; points) throw new InvalidOperationException("Not enough loyalty points");
/// customer.LoyaltyPoints -= points;
/// await _context.SaveChangesAsync();
/// </code>
///
/// <para>
/// Called <i>after</i> the order total had been computed and committed. So: the points bought
/// nothing (<b>M11</b> — "the points are destroyed"), nothing anywhere incremented the column so
/// they could never be earned in the first place (<b>M12</b>), the number redeemed was not
/// recorded so "where did my points go?" had no answer (<b>M11</b>), and the insufficiency check
/// threw a 500 after the order and every stock decrement were already committed (<b>D4</b>).
/// </para>
///
/// <para>
/// Mutating methods return the <see cref="PointsLedgerEntry"/> they imply rather than writing it,
/// exactly as <c>Product</c> returns its <c>StockMovement</c> — so the caller cannot move a
/// balance without producing the log entry that explains it.
/// </para>
/// </summary>
public sealed class PointsAccount : AggregateRoot
{
    public Guid CustomerId { get; private init; }

    /// <summary>
    /// The current balance.
    ///
    /// <para>
    /// <b>A column, and also a SUM.</b> The roadmap for this phase said "the balance is a
    /// <c>SUM</c>, never a column", aimed at Onyx's counter-with-no-log. Building it revealed
    /// that a pure <c>SUM</c> cannot carry the invariant: "the balance never goes negative" has
    /// to be enforced against a row under a concurrency token, and two simultaneous redemptions
    /// of the last 500 points both compute the same <c>SUM</c> and both pass. So the balance is
    /// state on this row — and <c>PointsIntegrityCheckJob</c> asserts
    /// <c>SUM(Delta) == Balance</c> on every pass.
    /// </para>
    ///
    /// <para>
    /// That is the same arrangement Catalog uses for <c>Product.Stock</c> against
    /// <c>StockMovement</c>, and it is the whole difference from Onyx: the counter is not the
    /// source of truth, it is an enforceable projection of a log that <i>is</i> — and something
    /// checks that they agree. Onyx had a counter, no log, and nothing checking anything.
    /// </para>
    /// </summary>
    public int Balance { get; private set; }

    /// <summary>
    /// Closed accounts belong to anonymised customers. Terminal, like
    /// <c>CustomerStatus.Anonymised</c>, and for the same reason.
    /// </summary>
    public bool IsClosed { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; private init; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    private PointsAccount() { }

    public static PointsAccount Open(Guid customerId, DateTimeOffset nowUtc)
    {
        var account = new PointsAccount
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Balance = 0,
            OpenedAtUtc = nowUtc
        };

        account.RaiseDomainEvent(new PointsAccountOpenedEvent(account.Id, customerId, nowUtc));

        return account;
    }

    // ---------------------------------------------------------------- earning (Onyx M12)

    /// <summary>
    /// Credits points earned on qualifying spend.
    ///
    /// <para>
    /// The caller computes the amount with <see cref="EarnRule.PointsFor"/> and states what
    /// caused it. Idempotent at the database on <c>(AccountId, Reference, Reason)</c>, so a
    /// redelivered "order paid" message credits once — the same mechanism that makes a retried
    /// stock deduction safe.
    /// </para>
    /// </summary>
    public ErrorOr<PointsLedgerEntry> Earn(int points, string reference, DateTimeOffset nowUtc) =>
        Credit(points, PointsReason.Earned, reference, nowUtc);

    /// <summary>
    /// Returns points a checkout spent but did not complete.
    ///
    /// <para>
    /// For expiry purposes these count as earned <i>now</i> rather than when they originally
    /// were — see <see cref="ExpirableBefore"/>. That is slightly generous, and deliberately so:
    /// the customer is being given back points they tried to spend on a checkout that failed for
    /// our reasons, and a scheme that also shortened their life would be charging them for our
    /// failure.
    /// </para>
    /// </summary>
    public ErrorOr<PointsLedgerEntry> Return(int points, string reference, DateTimeOffset nowUtc) =>
        Credit(points, PointsReason.Returned, reference, nowUtc);

    // ---------------------------------------------------------------- redemption (M11, D4)

    /// <summary>
    /// Checks whether a redemption is possible, without changing anything.
    ///
    /// <para>
    /// <b>This is the D4 fix.</b> Onyx checked sufficiency inside the debit, which ran after the
    /// order was committed — so an insufficient balance was a 500 with the order and all its
    /// stock decrements standing. Here the quote asks this first, gets a reason, and prices the
    /// basket accordingly; the debit happens later, in the transaction that confirms the
    /// session.
    /// </para>
    ///
    /// <para>
    /// The same rule is used by both, which is the point: <see cref="Redeem"/> calls this too,
    /// so a quote cannot promise a discount the debit then refuses. Onyx <b>M18</b> is the
    /// warning — one availability rule written twice, as <c>&lt;=</c> and <c>&lt;</c>, and two
    /// screens disagreeing about the same product.
    /// </para>
    /// </summary>
    public ErrorOr<Success> CanRedeem(int points)
    {
        if (IsClosed)
        {
            return PointsErrors.AccountClosed;
        }

        if (points <= 0)
        {
            return PointsErrors.PointsMustBePositive;
        }

        if (!PointsValue.IsRedeemable(points))
        {
            return PointsErrors.BelowMinimumRedemption(PointsValue.MinimumRedemption);
        }

        return points > Balance
            ? PointsErrors.InsufficientPoints(points, Balance)
            : Result.Success;
    }

    public ErrorOr<PointsLedgerEntry> Redeem(int points, string reference, DateTimeOffset nowUtc)
    {
        var allowed = CanRedeem(points);

        if (allowed.IsError)
        {
            return allowed.Errors;
        }

        return Debit(points, PointsReason.Redeemed, reference, nowUtc);
    }

    // ---------------------------------------------------------------- reversal and expiry

    /// <summary>
    /// Takes back points granted by an earning that has since been refunded.
    ///
    /// <para>
    /// <paramref name="pointsOriginallyEarned"/> is supplied by the caller from the ledger, and
    /// the reversal is <b>clamped to the current balance</b>. That clamp is a real decision: a
    /// customer who earned 500 points and spent 400 of them has 100 left, and a refund cannot
    /// claw back points that are already gone without pushing the balance negative — which the
    /// aggregate will not represent. The shortfall is absorbed rather than carried as a debt,
    /// because a negative loyalty balance is a concept this scheme does not have and inventing
    /// one to handle a refund edge case would be worse than the rounding.
    /// </para>
    /// </summary>
    public ErrorOr<PointsLedgerEntry> ReverseEarning(
        int pointsOriginallyEarned,
        string reference,
        DateTimeOffset nowUtc)
    {
        if (IsClosed)
        {
            return PointsErrors.AccountClosed;
        }

        if (pointsOriginallyEarned <= 0)
        {
            return PointsErrors.PointsMustBePositive;
        }

        var reversible = Math.Min(pointsOriginallyEarned, Balance);

        return reversible == 0
            ? Debit(0, PointsReason.Reversed, reference, nowUtc)
            : Debit(reversible, PointsReason.Reversed, reference, nowUtc);
    }

    /// <summary>
    /// How many points are old enough to lapse, given a cutoff.
    ///
    /// <para>
    /// <b>The insight that avoids tracking individual earning batches.</b> Points are consumed
    /// oldest-first, so whatever is left in an account is always the <i>most recently</i>
    /// credited. That makes the amount still alive from before a cutoff exactly:
    /// </para>
    ///
    /// <code>
    /// max(0, creditedBeforeCutoff - everythingConsumedSince)
    /// </code>
    ///
    /// <para>
    /// — two indexed sums over the ledger, and no lot entities, no per-batch remaining counters,
    /// and nothing mutable on a ledger row. The alternative design — a <c>PointsLot</c> child
    /// entity per earning with a <c>RemainingPoints</c> column — is what most schemes do, and it
    /// adds a third thing that has to stay consistent with the other two. FIFO consumption makes
    /// it unnecessary.
    /// </para>
    ///
    /// <para>
    /// "Credited" includes <see cref="PointsReason.Returned"/> entries, dated when the return
    /// happened. That is why a returned redemption effectively gets a fresh lifetime — see
    /// <see cref="Return"/>.
    /// </para>
    /// </summary>
    public static int ExpirableBefore(int creditedBeforeCutoff, int consumedTotal)
    {
        var alive = creditedBeforeCutoff - consumedTotal;
        return alive > 0 ? alive : 0;
    }

    /// <summary>
    /// Writes off lapsed points. Called by the expiry job with the figure
    /// <see cref="ExpirableBefore"/> produced.
    /// </summary>
    public ErrorOr<PointsLedgerEntry> Expire(int points, string reference, DateTimeOffset nowUtc)
    {
        if (IsClosed)
        {
            return PointsErrors.AccountClosed;
        }

        if (points <= 0)
        {
            return PointsErrors.PointsMustBePositive;
        }

        // Clamped, so a miscomputed cutoff can never drive the balance negative. The job's
        // arithmetic is tested, and this is the structural backstop regardless.
        return Debit(Math.Min(points, Balance), PointsReason.Expired, reference, nowUtc);
    }

    // ---------------------------------------------------------------- administration

    /// <summary>
    /// A manual correction or goodwill grant, attributed to the administrator making it.
    ///
    /// <para>
    /// The actor is required and recorded, for the same reason <c>Customer.ChangeRole</c>
    /// requires one: a change to something of monetary value with no attributable author is the
    /// audit record Onyx did not have (<b>A40</b>). <paramref name="points"/> is signed, so this
    /// covers both a goodwill credit and a correction.
    /// </para>
    /// </summary>
    public ErrorOr<PointsLedgerEntry> Adjust(
        int points,
        Guid adjustedByCustomerId,
        string note,
        DateTimeOffset nowUtc)
    {
        if (IsClosed)
        {
            return PointsErrors.AccountClosed;
        }

        if (adjustedByCustomerId == Guid.Empty)
        {
            return PointsErrors.ActorRequired;
        }

        if (points == 0)
        {
            return PointsErrors.PointsMustBePositive;
        }

        var reference = $"adjust:{adjustedByCustomerId:n}:{note}";

        return points > 0
            ? Credit(points, PointsReason.Adjusted, reference, nowUtc)
            : Debit(Math.Min(-points, Balance), PointsReason.Adjusted, reference, nowUtc);
    }

    /// <summary>
    /// Closes the account, forfeiting whatever remains.
    ///
    /// <para>
    /// Driven by <c>CustomerAnonymisedIntegrationEvent</c>. An erased customer cannot be sent a
    /// statement, cannot sign in, and cannot spend what is left — so the balance goes to zero
    /// through a recorded <see cref="PointsReason.Forfeited"/> entry rather than being
    /// abandoned at whatever it happened to be. The ledger survives: it is a financial record,
    /// and it holds a customer id and nothing else personal.
    /// </para>
    /// </summary>
    public ErrorOr<PointsLedgerEntry> Close(string reference, DateTimeOffset nowUtc)
    {
        if (IsClosed)
        {
            return PointsErrors.AccountClosed;
        }

        var forfeited = Balance;
        var entry = Debit(forfeited, PointsReason.Forfeited, reference, nowUtc);

        if (entry.IsError)
        {
            return entry.Errors;
        }

        IsClosed = true;
        ClosedAtUtc = nowUtc;

        RaiseDomainEvent(new PointsAccountClosedEvent(Id, CustomerId, forfeited, nowUtc));

        return entry;
    }

    // ---------------------------------------------------------------- the two primitives

    private ErrorOr<PointsLedgerEntry> Credit(
        int points,
        PointsReason reason,
        string reference,
        DateTimeOffset nowUtc)
    {
        if (IsClosed)
        {
            return PointsErrors.AccountClosed;
        }

        if (points <= 0)
        {
            return PointsErrors.PointsMustBePositive;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            return PointsErrors.ReferenceRequired;
        }

        Balance += points;

        return Record(points, reason, reference, nowUtc);
    }

    private ErrorOr<PointsLedgerEntry> Debit(
        int points,
        PointsReason reason,
        string reference,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return PointsErrors.ReferenceRequired;
        }

        if (points < 0)
        {
            return PointsErrors.PointsMustBePositive;
        }

        // The invariant, structurally. The callers that can legitimately ask for more than the
        // balance (reversal, expiry, forfeiture) clamp before getting here; anything else
        // reaching this is a bug, and failing loudly beats a negative balance.
        if (points > Balance)
        {
            return PointsErrors.InsufficientPoints(points, Balance);
        }

        Balance -= points;

        return Record(-points, reason, reference, nowUtc);
    }

    private PointsLedgerEntry Record(
        int delta,
        PointsReason reason,
        string reference,
        DateTimeOffset nowUtc)
    {
        RaiseDomainEvent(new PointsMovedEvent(
            Id, CustomerId, delta, reason, reference, Balance, nowUtc));

        return PointsLedgerEntry.Create(
            Id, CustomerId, delta, reason, reference, Balance, nowUtc);
    }
}
