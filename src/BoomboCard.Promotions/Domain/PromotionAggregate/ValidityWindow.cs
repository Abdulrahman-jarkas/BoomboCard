using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Promotions.Domain.PromotionAggregate;

/// <summary>
/// When a promotion is usable: a half-open interval in UTC, <c>[StartsAtUtc, EndsAtUtc)</c>.
///
/// <para>
/// <b>This value object exists because of one of the better bugs in the register.</b> Onyx held
/// its promo expiry as a date with a comment:
/// </para>
///
/// <code>
/// ["WELCOME10"] = new DateTime(2026, 12, 31),
/// ...
/// // Codes remain valid through the end of the listed day.
/// if (DateTime.Now > expiresOn) return 0m;
/// </code>
///
/// <para>
/// Three separate defects in four lines. The comment says "through the end of the listed day"
/// and the code compares against <b>midnight at the start</b> of it, so the code died a full day
/// early (<b>M14</b>'s sibling). <c>DateTime.Now</c> is server local time compared against an
/// unspecified-kind <c>DateTime</c>, so the actual cutover moved with the deployment's timezone
/// and crossed a different instant in summer than in winter (<b>M17</b>). And the same code's
/// expiry was written as <c>2026-11-30</c> in a second file, so from December the report showed
/// zero uptake on a code still costing margin (<b>M14</b>).
/// </para>
///
/// <para>
/// <b>The fix is to make the boundary impossible to get wrong rather than to document it.</b>
/// Both ends are explicit instants. <c>StartsAtUtc</c> is inclusive, <c>EndsAtUtc</c> is
/// exclusive, and that is stated once here and nowhere else. "Valid through 31 December" is
/// expressed as <c>EndsAtUtc = 2027-01-01T00:00:00Z</c> — the caller states the instant it means
/// instead of a date plus a convention about how to read it.
/// </para>
///
/// <para>
/// Half-open rather than closed because it composes: consecutive windows can abut with no gap
/// and no overlap, which a closed interval cannot do without arguing about the shared instant.
/// It is the same choice <c>ProductPrice</c> makes for effective dating (CONCEPTS §12).
/// </para>
/// </summary>
public sealed class ValidityWindow : ValueObject
{
    public DateTimeOffset StartsAtUtc { get; }

    public DateTimeOffset EndsAtUtc { get; }

    private ValidityWindow(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
    }

    public static ErrorOr<ValidityWindow> Create(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc)
    {
        if (endsAtUtc <= startsAtUtc)
        {
            return PromotionErrors.WindowEndsBeforeItStarts;
        }

        return new ValidityWindow(startsAtUtc, endsAtUtc);
    }

    /// <summary>
    /// The single definition of "is this promotion live". Every caller asks this; nobody writes
    /// their own comparison.
    ///
    /// Onyx M18 is the warning: its availability rule was written twice, as <c>&lt;=</c> in one
    /// place and <c>&lt;</c> in the other, and two screens disagreed about the same product.
    /// </summary>
    public bool Contains(DateTimeOffset instant) =>
        instant >= StartsAtUtc && instant < EndsAtUtc;

    public bool HasNotStartedAt(DateTimeOffset instant) => instant < StartsAtUtc;

    public bool HasEndedAt(DateTimeOffset instant) => instant >= EndsAtUtc;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return StartsAtUtc;
        yield return EndsAtUtc;
    }

    public override string ToString() => $"[{StartsAtUtc:O}, {EndsAtUtc:O})";
}
