using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.SharedKernel.Persistence.Inbox;

public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages");

        // Composite key, not a surrogate id with a unique index beside it. The identity of the
        // row IS the pair, and the duplicate-key violation on a second claim is the mechanism
        // rather than an accident of configuration.
        builder.HasKey(message => new { message.EventId, message.Consumer });

        builder.Property(message => message.Consumer).HasMaxLength(500).IsRequired();
        builder.Property(message => message.EventType).HasMaxLength(500).IsRequired();
    }
}
