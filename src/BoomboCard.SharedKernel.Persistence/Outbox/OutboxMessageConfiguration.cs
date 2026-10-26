using BoomboCard.SharedKernel.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.SharedKernel.Persistence.Outbox;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        builder.HasKey(message => message.Id);

        // 500 is genuinely enough for an assembly-qualified type name.
        builder.Property(message => message.Type).HasMaxLength(500).IsRequired();

        builder.Property(message => message.Payload).IsRequired();

        // The payload is serialised JSON and must be unbounded.
        //
        // This line exists because of a real bug that was already in the database. Each module's
        // DbContext applies `configurationBuilder.Properties<string>().HaveMaxLength(500)` as a
        // convention — a good default for the domain's own columns, and quietly wrong for this
        // one. The generated migration produced `Payload nvarchar(500)`, and SQL Server does not
        // truncate: it raises "String or binary data would be truncated" and fails the INSERT,
        // which fails the SaveChanges, which rolls back the entire command that produced the
        // event. A price change would simply have started returning 500 once an event's JSON
        // crossed 500 characters — and the existing events sit at roughly 400, so it was a field
        // or two away.
        //
        // SetMaxLength(null) rather than HasColumnType("nvarchar(max)"): explicit configuration
        // beats the convention, and leaving the length unset lets each provider pick its own
        // unbounded text type (nvarchar(max), text) instead of putting SQL Server syntax into
        // shared plumbing.
        builder.Property(message => message.Payload).Metadata.SetMaxLength(null);
        builder.Property(message => message.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(message => message.LastError).HasMaxLength(2000);

        // Lengths from the W3C Trace Context spec, not guesses: a traceparent is always exactly
        // 55 characters, and 512 is the tracestate size the spec tells implementations to
        // accept. Stated explicitly so neither one inherits the 500-character string convention
        // these modules apply — which is what silently truncated nothing here only because
        // 55 < 500, and would have quietly clipped a tracestate at 500.
        builder.Property(message => message.TraceParent)
            .HasMaxLength(TraceContext.TraceParentMaxLength);

        builder.Property(message => message.TraceState)
            .HasMaxLength(TraceContext.TraceStateMaxLength);

        // The dispatcher's only query: pending messages whose backoff has elapsed, oldest
        // first. Filtered so the index does not carry every published row forever.
        builder.HasIndex(message => new { message.Status, message.NextAttemptAtUtc })
            .HasDatabaseName("IX_OutboxMessages_Dispatch");
    }
}
