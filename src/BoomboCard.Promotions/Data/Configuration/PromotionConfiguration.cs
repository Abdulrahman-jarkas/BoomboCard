using BoomboCard.Promotions.Domain.PromotionAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Promotions.Data.Configuration;

internal sealed class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.ToTable("Promotions");

        builder.HasKey(promotion => promotion.Id);

        // The optimistic concurrency token, and here it is what enforces the total usage cap.
        // Two checkouts racing for the last use of a campaign both read RedemptionCount = 99,
        // both pass the check, and the version predicate makes exactly one of them win — the
        // loser affects zero rows, raises DbUpdateConcurrencyException, and is retried against
        // a count of 100. Identical in shape to Catalog's oversell prevention (Onyx D1).
        builder.Property(promotion => promotion.Version)
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(promotion => promotion.Description)
            .HasMaxLength(Promotion.MaxDescriptionLength)
            .IsRequired();

        builder.Property(promotion => promotion.Stacking)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(promotion => promotion.MaxRedemptions);
        builder.Property(promotion => promotion.MaxRedemptionsPerCustomer);
        builder.Property(promotion => promotion.RedemptionCount).IsRequired();
        builder.Property(promotion => promotion.CreatedAtUtc).IsRequired();

        // Code is an OWNED TYPE, not a value converter — the rule Catalog settled the hard way
        // (BUGS-AND-LESSONS §3): converters for single values you only read back, owned types
        // for anything you query, sort or index. Every checkout looks a promotion up by code, so
        // this has to be a real column the provider can match server-side.
        builder.OwnsOne(promotion => promotion.Code, code =>
        {
            code.Property(value => value.Value)
                .HasColumnName("Code")
                .HasMaxLength(PromotionCode.MaxLength)
                .IsRequired();

            // One row per code. Combined with PromotionCode normalising to upper case and
            // stripping whitespace, "welcome10" and " WELCOME10 " are the same promotion.
            //
            // This index is the structural half of closing M14. Onyx kept a code's expiry in one
            // dictionary and its rate in a separate switch statement keyed on the same string,
            // so two places had to agree about one code and did not — the expiry was 2026-12-31
            // in the pricing service and 2026-11-30 in the nightly report. One unique row holds
            // the rate, the window and the caps together, so there is nothing to diverge from.
            code.HasIndex(value => value.Value)
                .IsUnique()
                .HasDatabaseName("UX_Promotions_Code");
        });
        builder.Navigation(promotion => promotion.Code).IsRequired();

        builder.OwnsOne(promotion => promotion.Status, status =>
        {
            status.Property(value => value.Value)
                .HasColumnName("Status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
        });
        builder.Navigation(promotion => promotion.Status).IsRequired();

        builder.OwnsOne(promotion => promotion.Window, window =>
        {
            window.Property(value => value.StartsAtUtc)
                .HasColumnName("StartsAtUtc")
                .IsRequired();

            window.Property(value => value.EndsAtUtc)
                .HasColumnName("EndsAtUtc")
                .IsRequired();

            // The expiry sweeper's query: active promotions whose window has ended. An owned
            // type gives real columns, so this is an index seek rather than a table scan —
            // Onyx had no indexes anywhere at all (D11).
            window.HasIndex(value => value.EndsAtUtc)
                .HasDatabaseName("IX_Promotions_EndsAt");
        });
        builder.Navigation(promotion => promotion.Window).IsRequired();

        builder.OwnsOne(promotion => promotion.Discount, discount =>
        {
            discount.Property(value => value.Kind)
                .HasColumnName("DiscountKind")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            // Four decimal places, not the model-wide two: a rate of 0.075m is 7.5%, and
            // rounding it to 0.08m would quietly turn it into 8%. The money convention is right
            // for money and wrong for a multiplier — the same class of mistake as the 500-char
            // convention landing on the outbox payload (BUGS-AND-LESSONS §9), caught this time
            // by asking what the column actually holds.
            discount.Property(value => value.Rate)
                .HasColumnName("DiscountRate")
                .HasPrecision(9, 4)
                .IsRequired();

            discount.OwnsOne(value => value.Amount, money =>
            {
                money.Property(amount => amount.Amount).HasColumnName("DiscountAmount");
                money.Property(amount => amount.CurrencyCode)
                    .HasColumnName("DiscountCurrency")
                    .HasMaxLength(3);
            });
        });
        builder.Navigation(promotion => promotion.Discount).IsRequired();

        builder.OwnsOne(promotion => promotion.MinimumSpend, money =>
        {
            money.Property(value => value.Amount).HasColumnName("MinimumSpendAmount");
            money.Property(value => value.CurrencyCode)
                .HasColumnName("MinimumSpendCurrency")
                .HasMaxLength(3);
        });

        // Scope is an owned type whose product ids are an EF primitive collection — stored as
        // JSON in one column, not a child table and not a delimited string.
        //
        // A delimited string would be the wrong answer: it cannot be queried, and parsing it
        // back puts the format in two places. A child table would be defensible and is what a
        // scope that needed joining or indexing would want — but nothing joins on it. The set is
        // bounded at 200 ids and is only ever read as a whole, by the scope's own Includes check
        // while evaluating one basket. A table would buy query shapes nothing asks for, which is
        // the speculative generality A45 is about.
        builder.OwnsOne(promotion => promotion.Scope, scope =>
        {
            scope.Property(value => value.Kind)
                .HasColumnName("ScopeKind")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            // Mapped to the private field by name, because ProductIds is a read-only view and
            // EF needs something it can write. EF stores a primitive collection as JSON in a
            // single column, which is the right trade here: the set is small, bounded at 200,
            // and only ever read as a whole by the scope's own Includes check.
            scope.PrimitiveCollection<List<Guid>>("_productIds")
                .HasColumnName("ScopeProductIds");
        });
        builder.Navigation(promotion => promotion.Scope).IsRequired();

        // The admin list filters on status and pages by code.
        builder.HasIndex(promotion => promotion.CreatedAtUtc)
            .HasDatabaseName("IX_Promotions_CreatedAt");
    }
}
