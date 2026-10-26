using BoomboCard.Promotions.Domain.Redemptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Promotions.Data.Configuration;

internal sealed class PromotionRedemptionConfiguration
    : IEntityTypeConfiguration<PromotionRedemption>
{
    /// <summary>Matches <c>StockMovement.Reference</c>, which holds the same session id.</summary>
    public const int MaxReferenceLength = 200;

    public void Configure(EntityTypeBuilder<PromotionRedemption> builder)
    {
        builder.ToTable("PromotionRedemptions");

        builder.HasKey(redemption => redemption.Id);

        builder.Property(redemption => redemption.PromotionId).IsRequired();
        builder.Property(redemption => redemption.CustomerId).IsRequired();

        builder.Property(redemption => redemption.CheckoutSessionId)
            .HasMaxLength(MaxReferenceLength)
            .IsRequired();

        builder.Property(redemption => redemption.CustomerOrdinal).IsRequired();
        builder.Property(redemption => redemption.RedeemedAtUtc).IsRequired();
        builder.Property(redemption => redemption.ReleasedAtUtc);

        builder.Property(redemption => redemption.ReleaseReason)
            .HasConversion<string>()
            .HasMaxLength(20);

        // ------------------------------------------------------------ the two indexes that matter
        //
        // These are the point of this table. A usage cap is a count, and an application-level
        // count has a race between the look and the leap; these turn both caps into constraints
        // the database enforces.

        // 1. IDEMPOTENCY ON THE REFERENCE.
        //
        // One redemption per promotion per checkout session, so a retried checkout is a no-op
        // rather than a second use. Exactly the role Catalog's
        // (ProductId, Reference, Reason) index plays for stock, and what makes retry the cheapest
        // recovery layer when a later step in the saga fails.
        //
        // Onyx M5 is the counter-example: its payment retry loop sent no idempotency key at all,
        // so a timeout followed by a retry could charge twice — while the PR claimed "the gateway
        // call is idempotent on their end" (X9).
        builder.HasIndex(redemption => new { redemption.PromotionId, redemption.CheckoutSessionId })
            .IsUnique()
            .HasDatabaseName("UX_PromotionRedemptions_Session");

        // 2. THE PER-CUSTOMER CAP.
        //
        // CustomerOrdinal is this customer's n-th use, counting from 1, and making
        // (PromotionId, CustomerId, CustomerOrdinal) unique turns "at most N per customer" into
        // a plain unique index: the handler computes the ordinal as "uses so far plus one", so
        // two concurrent reservations that both read the same count compute the same ordinal and
        // collide. One wins; the loser is retried and reads the new count.
        //
        // The obvious alternative is a filtered unique index on (PromotionId, CustomerId) for
        // the cap-of-one case. It needs provider-specific SQL in a module that deliberately
        // references no provider, and it only works for a cap of one. That exact mistake was
        // made and reverted in Phase 1's address defaulting; the ordinal generalises and stays
        // provider-agnostic.
        builder.HasIndex(redemption => new
            {
                redemption.PromotionId,
                redemption.CustomerId,
                redemption.CustomerOrdinal
            })
            .IsUnique()
            .HasDatabaseName("UX_PromotionRedemptions_CustomerOrdinal");

        // The release sweeper looks rows up by session across all promotions, because it is told
        // a session id and nothing else.
        builder.HasIndex(redemption => redemption.CheckoutSessionId)
            .HasDatabaseName("IX_PromotionRedemptions_Session");

        // Campaign reporting: redemptions over time for one promotion.
        builder.HasIndex(redemption => new { redemption.PromotionId, redemption.RedeemedAtUtc })
            .HasDatabaseName("IX_PromotionRedemptions_PromotionRedeemedAt");
    }
}
