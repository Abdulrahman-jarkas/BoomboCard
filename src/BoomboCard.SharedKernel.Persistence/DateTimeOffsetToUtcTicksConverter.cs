using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BoomboCard.SharedKernel.Persistence;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as UTC ticks.
///
/// SQLite has no native date type: it keeps DateTimeOffset as TEXT and then refuses to ORDER BY
/// or range-compare it — "SQLite does not support expressions of type 'DateTimeOffset' in ORDER
/// BY clauses". That breaks every query this system depends on: the outbox dispatcher's
/// "pending and due" predicate, the export generator's oldest-first ordering, and the
/// effective-dated price lookup.
///
/// Storing UTC ticks as an integer makes comparison and ordering exact and index-friendly.
///
/// The offset is deliberately discarded: everything in this system is UTC by rule (TimeProvider
/// .GetUtcNow() everywhere), so there is no offset worth preserving. Onyx A29 is the opposite
/// mistake — it wrote DateTime.Now, server local time, into a field named OrderDateUtc, then
/// compared it against a local day boundary in a report.
///
/// Applied only when the provider is SQLite. PostgreSQL and SQL Server handle DateTimeOffset
/// natively and get the native type.
/// </summary>
public sealed class DateTimeOffsetToUtcTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public DateTimeOffsetToUtcTicksConverter()
        : base(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
    {
    }
}

public sealed class NullableDateTimeOffsetToUtcTicksConverter : ValueConverter<DateTimeOffset?, long?>
{
    public NullableDateTimeOffsetToUtcTicksConverter()
        : base(
            value => value == null ? null : value.Value.UtcTicks,
            ticks => ticks == null ? null : new DateTimeOffset(ticks.Value, TimeSpan.Zero))
    {
    }
}
