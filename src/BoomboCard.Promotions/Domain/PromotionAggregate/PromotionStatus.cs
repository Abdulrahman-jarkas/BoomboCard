using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Promotions.Domain.PromotionAggregate;

public enum PromotionStatusValue
{
    /// <summary>Being configured. Editable, and invisible to customers.</summary>
    Draft = 0,

    /// <summary>Live, subject to its window and its caps.</summary>
    Active = 1,

    /// <summary>Stopped by hand. Reversible.</summary>
    Paused = 2,

    /// <summary>
    /// Finished and <b>terminal</b> — the window passed, or the usage cap was reached. A closed
    /// campaign is a historical fact, and its ledger is what reporting reads.
    /// </summary>
    Closed = 3
}

/// <summary>
/// The campaign lifecycle, with the legal transitions held in the value object rather than in
/// scattered if-statements.
///
/// <para>
/// <b>Draft is the whole point of having a lifecycle here.</b> Onyx had no campaign management
/// at all: its promotions were two entries in a <c>private static readonly Dictionary</c>, so
/// creating one meant a code change and a deployment, and the one shipped as <c>SUMMER25</c>
/// expired on 2026-09-01 — <b>before the audit that found it</b> (<b>M16</b>). There was no
/// state in which a campaign could be configured and reviewed before customers could use it,
/// because there was no state at all.
/// </para>
///
/// <para>
/// <c>Closed</c> is terminal, and that is deliberate rather than incidental. Reopening an
/// exhausted campaign would let a "first 100 customers" promotion serve 150, which is the kind
/// of quiet promise-breaking that is worse than refusing. A new campaign is a new promotion with
/// a new code and its own ledger.
/// </para>
/// </summary>
public sealed class PromotionStatus : ValueObject
{
    public PromotionStatusValue Value { get; }

    private PromotionStatus(PromotionStatusValue value) => Value = value;

    public static PromotionStatus Draft => new(PromotionStatusValue.Draft);

    public static PromotionStatus From(PromotionStatusValue value) => new(value);

    public bool IsDraft => Value is PromotionStatusValue.Draft;

    public bool IsActive => Value is PromotionStatusValue.Active;

    public bool IsPaused => Value is PromotionStatusValue.Paused;

    public bool IsClosed => Value is PromotionStatusValue.Closed;

    public ErrorOr<PromotionStatus> Activate() =>
        Value switch
        {
            PromotionStatusValue.Active => PromotionStatusErrors.AlreadyActive,
            PromotionStatusValue.Closed => PromotionStatusErrors.Closed,
            _ => new PromotionStatus(PromotionStatusValue.Active)
        };

    public ErrorOr<PromotionStatus> Pause() =>
        Value switch
        {
            PromotionStatusValue.Active => new PromotionStatus(PromotionStatusValue.Paused),
            PromotionStatusValue.Paused => PromotionStatusErrors.AlreadyPaused,
            _ => PromotionStatusErrors.NotActive
        };

    public ErrorOr<PromotionStatus> Close() =>
        Value switch
        {
            PromotionStatusValue.Closed => PromotionStatusErrors.AlreadyClosed,
            _ => new PromotionStatus(PromotionStatusValue.Closed)
        };

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString();
}

public static class PromotionStatusErrors
{
    public static Error AlreadyActive =>
        Error.Conflict("PromotionStatus.AlreadyActive", "The promotion is already active.");

    public static Error AlreadyPaused =>
        Error.Conflict("PromotionStatus.AlreadyPaused", "The promotion is already paused.");

    public static Error AlreadyClosed =>
        Error.Conflict("PromotionStatus.AlreadyClosed", "The promotion is already closed.");

    public static Error NotActive =>
        Error.Conflict("PromotionStatus.NotActive", "Only an active promotion can be paused.");

    public static Error Closed =>
        Error.Conflict(
            "PromotionStatus.Closed",
            "A closed promotion cannot be reopened. Create a new campaign instead.");
}
