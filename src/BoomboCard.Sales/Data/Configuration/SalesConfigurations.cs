using BoomboCard.Sales.Domain.CheckoutSessionAggregate;
using BoomboCard.Sales.Domain.Numbering;
using BoomboCard.Sales.Domain.OrderAggregate;
using BoomboCard.Sales.Domain.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoomboCard.Sales.Data.Configuration;

/// <summary>
/// Maps a <c>Money</c> owned by something, as two columns with a shared prefix. Used eleven
/// times across this module, so it exists rather than being retyped — and so the column naming
/// is one decision instead of eleven.
/// </summary>
internal static class MoneyMapping
{
    public static void Map<TOwner>(
        OwnedNavigationBuilder<TOwner, SharedKernel.Money> builder,
        string prefix)
        where TOwner : class
    {
        builder.Property(money => money.Amount).HasColumnName($"{prefix}Amount");
        builder.Property(money => money.CurrencyCode)
            .HasColumnName($"{prefix}Currency")
            .HasMaxLength(3);
    }
}

internal sealed class CheckoutSessionConfiguration : IEntityTypeConfiguration<CheckoutSession>
{
    public void Configure(EntityTypeBuilder<CheckoutSession> builder)
    {
        builder.ToTable("CheckoutSessions");

        builder.HasKey(session => session.Id);

        builder.Property(session => session.Version).IsConcurrencyToken().IsRequired();

        builder.Property(session => session.CustomerId).IsRequired();
        builder.Property(session => session.CountryCode).HasMaxLength(2).IsRequired();
        builder.Property(session => session.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(session => session.StartedAtUtc).IsRequired();
        builder.Property(session => session.ExpiresAtUtc).IsRequired();
        builder.Property(session => session.HoldsPlacedAtUtc);
        builder.Property(session => session.PromotionId);
        builder.Property(session => session.PointsRedeemed).IsRequired();

        builder.OwnsOne(session => session.Status, status =>
        {
            status.Property(value => value.Value)
                .HasColumnName("Status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
        });
        builder.Navigation(session => session.Status).IsRequired();

        builder.OwnsOne(s => s.MerchandiseSubtotal, m => MoneyMapping.Map(m, "MerchandiseSubtotal"));
        builder.OwnsOne(s => s.DiscountedMerchandise, m => MoneyMapping.Map(m, "DiscountedMerchandise"));
        builder.OwnsOne(s => s.Shipping, m => MoneyMapping.Map(m, "Shipping"));
        builder.OwnsOne(s => s.TaxableBase, m => MoneyMapping.Map(m, "TaxableBase"));
        builder.OwnsOne(s => s.Tax, m => MoneyMapping.Map(m, "Tax"));
        builder.OwnsOne(s => s.Total, m => MoneyMapping.Map(m, "Total"));

        builder.Navigation(s => s.MerchandiseSubtotal).IsRequired();
        builder.Navigation(s => s.DiscountedMerchandise).IsRequired();
        builder.Navigation(s => s.Shipping).IsRequired();
        builder.Navigation(s => s.TaxableBase).IsRequired();
        builder.Navigation(s => s.Tax).IsRequired();
        builder.Navigation(s => s.Total).IsRequired();

        builder.HasMany(session => session.Lines)
            .WithOne()
            .HasForeignKey(line => line.CheckoutSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(session => session.Adjustments)
            .WithOne()
            .HasForeignKey(adjustment => adjustment.CheckoutSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(CheckoutSession.Lines))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(CheckoutSession.Adjustments))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // The expiry sweeper's query: live sessions past their deadline. Ordered and capped, so
        // this is an index seek. It is the query recovery layer 2 depends on, which makes it the
        // one query in this module whose index is not optional.
        builder.HasIndex(session => session.ExpiresAtUtc)
            .HasDatabaseName("IX_CheckoutSessions_ExpiresAt");

        builder.HasIndex(session => new { session.CustomerId, session.StartedAtUtc })
            .HasDatabaseName("IX_CheckoutSessions_Customer");
    }
}

internal sealed class CheckoutLineConfiguration : IEntityTypeConfiguration<CheckoutLine>
{
    public void Configure(EntityTypeBuilder<CheckoutLine> builder)
    {
        builder.ToTable("CheckoutLines");

        builder.HasKey(line => line.Id);

        builder.Property(line => line.CheckoutSessionId).IsRequired();
        builder.Property(line => line.ProductId).IsRequired();
        builder.Property(line => line.Sku).HasMaxLength(32).IsRequired();
        builder.Property(line => line.Name).HasMaxLength(200).IsRequired();
        builder.Property(line => line.Quantity).IsRequired();

        // The captured price id — the proof of what the customer was shown (A6).
        builder.Property(line => line.PriceId).IsRequired();

        builder.OwnsOne(line => line.UnitPrice, m => MoneyMapping.Map(m, "UnitPrice"));
        builder.OwnsOne(line => line.LineTotal, m => MoneyMapping.Map(m, "LineTotal"));
        builder.Navigation(line => line.UnitPrice).IsRequired();
        builder.Navigation(line => line.LineTotal).IsRequired();

        // One line per product per session. BuildLines collapses duplicates before pricing,
        // because the stock idempotency key is per product and reference — two lines for one
        // product would collide with themselves. This is the backstop.
        builder.HasIndex(line => new { line.CheckoutSessionId, line.ProductId })
            .IsUnique()
            .HasDatabaseName("UX_CheckoutLines_SessionProduct");
    }
}

internal sealed class QuoteAdjustmentConfiguration : IEntityTypeConfiguration<QuoteAdjustment>
{
    public void Configure(EntityTypeBuilder<QuoteAdjustment> builder)
    {
        builder.ToTable("QuoteAdjustments");

        builder.HasKey(adjustment => adjustment.Id);

        builder.Property(adjustment => adjustment.CheckoutSessionId).IsRequired();

        builder.Property(adjustment => adjustment.Kind)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(adjustment => adjustment.Description).HasMaxLength(200).IsRequired();

        // Signed decimals, not owned Money — a discount is genuinely negative and Money cannot
        // represent one. The arithmetic happens in Money; the sign is applied here.
        builder.Property(adjustment => adjustment.Amount).IsRequired();
        builder.Property(adjustment => adjustment.BasisAmount).IsRequired();

        builder.Property(adjustment => adjustment.PromotionId);
        builder.Property(adjustment => adjustment.PointsRedeemed);

        builder.HasIndex(adjustment => adjustment.CheckoutSessionId)
            .HasDatabaseName("IX_QuoteAdjustments_Session");
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        builder.HasKey(order => order.Id);

        builder.Property(order => order.Version).IsConcurrencyToken().IsRequired();

        builder.Property(order => order.CustomerId).IsRequired();
        builder.Property(order => order.CheckoutSessionId).IsRequired();
        builder.Property(order => order.CountryCode).HasMaxLength(2).IsRequired();
        builder.Property(order => order.PlacedAtUtc).IsRequired();
        builder.Property(order => order.PaidAtUtc);
        builder.Property(order => order.ShippedAtUtc);
        builder.Property(order => order.DeliveredAtUtc);
        builder.Property(order => order.CancelledAtUtc);
        builder.Property(order => order.RefundedAtUtc);
        builder.Property(order => order.PromotionId);
        builder.Property(order => order.PointsRedeemed).IsRequired();

        // The order number is an OWNED TYPE, not a converter — the rule Catalog settled the hard
        // way (BUGS-AND-LESSONS §3). Customers look orders up by number, so it has to be a real
        // column the provider can match server-side.
        builder.OwnsOne(order => order.Number, number =>
        {
            number.Property(value => value.Value)
                .HasColumnName("OrderNumber")
                .HasMaxLength(20)
                .IsRequired();

            // One order per number. Onyx D8's static counter reset to 1000 on every restart and
            // was per-process, so numbers repeated across restarts and across instances — and
            // nothing would have noticed, because the number was never written to the order at
            // all, only logged.
            number.HasIndex(value => value.Value)
                .IsUnique()
                .HasDatabaseName("UX_Orders_OrderNumber");
        });
        builder.Navigation(order => order.Number).IsRequired();

        builder.OwnsOne(order => order.Status, status =>
        {
            status.Property(value => value.Value)
                .HasColumnName("Status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
        });
        builder.Navigation(order => order.Status).IsRequired();

        builder.OwnsOne(o => o.MerchandiseSubtotal, m => MoneyMapping.Map(m, "MerchandiseSubtotal"));
        builder.OwnsOne(o => o.DiscountedMerchandise, m => MoneyMapping.Map(m, "DiscountedMerchandise"));
        builder.OwnsOne(o => o.Shipping, m => MoneyMapping.Map(m, "Shipping"));
        builder.OwnsOne(o => o.Tax, m => MoneyMapping.Map(m, "Tax"));
        builder.OwnsOne(o => o.Total, m => MoneyMapping.Map(m, "Total"));
        builder.OwnsOne(o => o.QualifyingSpend, m => MoneyMapping.Map(m, "QualifyingSpend"));

        builder.Navigation(o => o.MerchandiseSubtotal).IsRequired();
        builder.Navigation(o => o.DiscountedMerchandise).IsRequired();
        builder.Navigation(o => o.Shipping).IsRequired();
        builder.Navigation(o => o.Tax).IsRequired();
        builder.Navigation(o => o.Total).IsRequired();
        builder.Navigation(o => o.QualifyingSpend).IsRequired();

        builder.HasMany(order => order.Items)
            .WithOne()
            .HasForeignKey(item => item.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(order => order.Adjustments)
            .WithOne()
            .HasForeignKey(adjustment => adjustment.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(order => order.Payments)
            .WithOne()
            .HasForeignKey(payment => payment.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Order.Items))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(Order.Adjustments))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(Order.Payments))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // One order per checkout session. The confirm handler checks for an existing order
        // first and returns it, so a retried confirm is a no-op — this index is what makes that
        // a guarantee rather than a race. Same reference-idempotency as everywhere else.
        builder.HasIndex(order => order.CheckoutSessionId)
            .IsUnique()
            .HasDatabaseName("UX_Orders_CheckoutSession");

        // The customer's order history, newest first, keyset paged.
        builder.HasIndex(order => new { order.CustomerId, order.PlacedAtUtc })
            .HasDatabaseName("IX_Orders_CustomerPlacedAt");
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.OrderId).IsRequired();
        builder.Property(item => item.ProductId).IsRequired();
        builder.Property(item => item.Sku).HasMaxLength(32).IsRequired();
        builder.Property(item => item.Name).HasMaxLength(200).IsRequired();
        builder.Property(item => item.Quantity).IsRequired();
        builder.Property(item => item.QuantityShipped).IsRequired();
        builder.Property(item => item.PriceId).IsRequired();

        builder.OwnsOne(item => item.UnitPrice, m => MoneyMapping.Map(m, "UnitPrice"));
        builder.OwnsOne(item => item.LineTotal, m => MoneyMapping.Map(m, "LineTotal"));
        builder.Navigation(item => item.UnitPrice).IsRequired();
        builder.Navigation(item => item.LineTotal).IsRequired();

        builder.HasIndex(item => item.ProductId).HasDatabaseName("IX_OrderItems_ProductId");
    }
}

internal sealed class OrderPaymentConfiguration : IEntityTypeConfiguration<OrderPayment>
{
    public void Configure(EntityTypeBuilder<OrderPayment> builder)
    {
        builder.ToTable("OrderPayments");

        builder.HasKey(payment => payment.Id);

        builder.Property(payment => payment.OrderId).IsRequired();
        builder.Property(payment => payment.Reference).HasMaxLength(200).IsRequired();
        builder.Property(payment => payment.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(payment => payment.CapturedAtUtc).IsRequired();

        // Money is a column here rather than an owned Money, because a capture's amount is a
        // historical fact about what the provider took rather than a value the domain computes
        // with. Precision is set explicitly all the same — Onyx X10 claimed "all monetary values
        // use decimal(18,2)" while configuring precision nowhere, so SQLite stored every amount
        // as a float and 19.99 came back as 19.989999999999998.
        builder.Property(payment => payment.Amount).HasPrecision(18, 2).IsRequired();

        // THE duplicate-payment guarantee, and the reason OrderPayment is a row at all.
        //
        // One capture per provider reference, across every order. Global rather than scoped to
        // the order on purpose: a provider reference identifies a transaction at the provider,
        // so the same one appearing against two orders is a bug worth failing on rather than a
        // coincidence worth allowing.
        //
        // Onyx M3 ("no duplicate-payment protection") and M5 ("retries can double-charge"): its
        // payment path had no idempotency key at all, and its PR asserted the retry was safe
        // because "the gateway call is idempotent on their end" — with nothing in the request
        // identifying the payment.
        builder.HasIndex(payment => payment.Reference)
            .IsUnique()
            .HasDatabaseName("UX_OrderPayments_Reference");
    }
}

internal sealed class OrderAdjustmentConfiguration : IEntityTypeConfiguration<OrderAdjustment>
{
    public void Configure(EntityTypeBuilder<OrderAdjustment> builder)
    {
        builder.ToTable("OrderAdjustments");

        builder.HasKey(adjustment => adjustment.Id);

        builder.Property(adjustment => adjustment.OrderId).IsRequired();

        builder.Property(adjustment => adjustment.Kind)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(adjustment => adjustment.Description).HasMaxLength(200).IsRequired();
        builder.Property(adjustment => adjustment.Amount).IsRequired();
        builder.Property(adjustment => adjustment.BasisAmount).IsRequired();
        builder.Property(adjustment => adjustment.PromotionId);
        builder.Property(adjustment => adjustment.PointsRedeemed);

        // Campaign reporting reconciles promotion redemptions against actual orders.
        builder.HasIndex(adjustment => adjustment.PromotionId)
            .HasDatabaseName("IX_OrderAdjustments_PromotionId");
    }
}

internal sealed class OrderNumberSequenceConfiguration
    : IEntityTypeConfiguration<OrderNumberSequence>
{
    public void Configure(EntityTypeBuilder<OrderNumberSequence> builder)
    {
        builder.ToTable("OrderNumberSequence");

        builder.HasKey(sequence => sequence.Id);

        // The concurrency token is the entire mechanism. Two orders placed simultaneously both
        // read the same NextValue, both increment, and exactly one commits; the loser is retried
        // by ResilientCommandDispatcher against the new value.
        //
        // A database sequence would be the obvious alternative and is provider-specific —
        // HasSequence generates CREATE SEQUENCE, which SQLite does not have, and this module
        // references no provider on purpose. See OrderNumberSequence.
        builder.Property(sequence => sequence.Version).IsConcurrencyToken().IsRequired();

        builder.Property(sequence => sequence.NextValue).IsRequired();
    }
}
