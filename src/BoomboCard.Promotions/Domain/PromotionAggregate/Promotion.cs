using BoomboCard.Promotions.Contracts;
using BoomboCard.Promotions.Domain.PromotionAggregate.Events;
using BoomboCard.Promotions.Domain.Redemptions;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Promotions.Domain.PromotionAggregate;

/// <summary>
/// A discount campaign: a code, what it takes off, when it is usable, what it needs, what it
/// applies to, and how many times it may be used.
///
/// Invariants it holds:
///  - A code is unique and normalised.
///  - Terms can only change while the promotion is a draft.
///  - A promotion is usable only when Active, inside its window, under both caps, over its
///    minimum spend, and against a basket its scope matches.
///  - The redemption count never exceeds the total cap.
///  - Closed is terminal.
///
/// <para>
/// <b>What this replaces.</b> Onyx's entire promotions capability was this:
/// </para>
///
/// <code>
/// private static readonly Dictionary&lt;string, DateTime&gt; PromoCodes = new(...)
/// {
///     ["WELCOME10"] = new DateTime(2026, 12, 31),
///     ["SUMMER25"] = new DateTime(2026, 9, 1),
/// };
/// </code>
///
/// <para>
/// Six findings in nine lines. Creating a campaign meant a code change and a deployment
/// (<b>M16</b>). <c>SUMMER25</c> shipped already expired (<b>M16</b>). The rate lived in a
/// separate <c>switch</c> keyed on the same string, so two places had to agree and did not
/// (<b>M14</b>) — and the same code's expiry was written as a different date in a second file,
/// so from December the report showed zero uptake on a code still costing margin. The expiry
/// comparison used <c>DateTime.Now</c> against an unspecified-kind date (<b>M17</b>). An
/// unrecognised code returned a 0% rate and the order went through silently (<b>M15</b>). And
/// there were no minimum spends, no usage caps, no per-customer limits, no start dates, no
/// scope, no stacking rules and no reporting at all (<c>ISSUES.md</c> §7).
/// </para>
///
/// <para>
/// Mutating methods that imply a ledger row return the <see cref="PromotionRedemption"/> they
/// imply rather than writing it, exactly as <c>Product</c> returns its <c>StockMovement</c> — so
/// the caller cannot produce the state change without the log entry.
/// </para>
/// </summary>
public sealed class Promotion : AggregateRoot
{
    public const int MaxDescriptionLength = 200;
    public const int MaxScopedProducts = 200;

    public PromotionCode Code { get; private set; } = null!;

    /// <summary>What a merchandiser calls it. Shown on the quote line the customer sees.</summary>
    public string Description { get; private set; } = string.Empty;

    public Discount Discount { get; private set; } = null!;

    public ValidityWindow Window { get; private set; } = null!;

    public PromotionStatus Status { get; private set; } = PromotionStatus.Draft;

    public PromotionScope Scope { get; private set; } = PromotionScope.EntireBasket;

    public StackingPolicy Stacking { get; private set; } = StackingPolicy.Exclusive;

    /// <summary>
    /// The basket subtotal a customer must reach. Null for no minimum.
    ///
    /// Denominated, so a minimum-spend promotion is currency-specific even when its discount is
    /// a percentage — "10% off orders over 50" means something different in USD and AED.
    /// </summary>
    public Money? MinimumSpend { get; private set; }

    /// <summary>Total uses allowed across all customers. Null for unlimited.</summary>
    public int? MaxRedemptions { get; private set; }

    /// <summary>Uses allowed per customer. Null for unlimited.</summary>
    public int? MaxRedemptionsPerCustomer { get; private set; }

    /// <summary>
    /// Live uses, net of releases.
    ///
    /// <para>
    /// State on the aggregate rather than a <c>COUNT</c> over the ledger, because it is the
    /// quantity the total cap is enforced against and that enforcement has to happen under the
    /// concurrency token. Two checkouts racing for the last use of a campaign both read the same
    /// count, both pass the check, and the version predicate is what makes exactly one of them
    /// win — identical in shape to Catalog's oversell prevention (CONCEPTS §8).
    /// </para>
    /// </summary>
    public int RedemptionCount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    private Promotion() { }

    public static ErrorOr<Promotion> Create(
        PromotionCode code,
        string description,
        Discount discount,
        ValidityWindow window,
        PromotionScope scope,
        StackingPolicy stacking,
        Money? minimumSpend,
        int? maxRedemptions,
        int? maxRedemptionsPerCustomer,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(description)
            || description.Trim().Length > MaxDescriptionLength)
        {
            return Error.Validation(
                "Promotion.DescriptionRequired",
                $"A description is required and may be at most {MaxDescriptionLength} characters.");
        }

        if (maxRedemptions is < 1 || maxRedemptionsPerCustomer is < 1)
        {
            return PromotionErrors.CapMustBePositive;
        }

        if (scope.Kind is PromotionScopeKind.SpecificProducts
            && scope.ProductIds.Count > MaxScopedProducts)
        {
            return Error.Validation(
                "Promotion.TooManyScopedProducts",
                $"A promotion may name at most {MaxScopedProducts} products.");
        }

        // A fixed-amount discount and a minimum spend in different currencies could never both
        // apply, so the campaign would be silently unusable rather than wrong — which is worse,
        // because nothing would ever report an error.
        if (discount.Kind is DiscountKind.FixedAmount
            && minimumSpend is not null
            && discount.Amount!.CurrencyCode != minimumSpend.CurrencyCode)
        {
            return PromotionErrors.CurrencyMismatch(
                discount.Amount.CurrencyCode,
                minimumSpend.CurrencyCode);
        }

        var promotion = new Promotion
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = description.Trim(),
            Discount = discount,
            Window = window,
            Scope = scope,
            Stacking = stacking,
            MinimumSpend = minimumSpend,
            MaxRedemptions = maxRedemptions,
            MaxRedemptionsPerCustomer = maxRedemptionsPerCustomer,
            // Draft, always. A campaign goes live by a separate, deliberate act — which is the
            // state Onyx had no way to express, so its one shipped promotion was live and
            // already expired from the moment it was written (M16).
            Status = PromotionStatus.Draft,
            RedemptionCount = 0,
            CreatedAtUtc = nowUtc
        };

        promotion.RaiseDomainEvent(new PromotionCreatedEvent(
            promotion.Id, code.Value, promotion.Description, nowUtc));

        return promotion;
    }

    // ---------------------------------------------------------------- configuration

    /// <summary>
    /// Replaces the terms. Draft only.
    ///
    /// <para>
    /// The restriction is a business rule, not caution. A live campaign's terms are something
    /// customers may already have been shown and acted on, and silently changing the minimum
    /// spend under them is the same class of wrong as Onyx <b>A6</b> — showing one price and
    /// charging another. To change a live campaign, close it and create its successor; the
    /// ledger then attributes each redemption to the terms that were actually in force.
    /// </para>
    /// </summary>
    public ErrorOr<Success> Reconfigure(
        string description,
        Discount discount,
        ValidityWindow window,
        PromotionScope scope,
        StackingPolicy stacking,
        Money? minimumSpend,
        int? maxRedemptions,
        int? maxRedemptionsPerCustomer,
        DateTimeOffset nowUtc)
    {
        if (!Status.IsDraft)
        {
            return PromotionErrors.OnlyDraftsCanBeEdited;
        }

        // Re-validated through the factory rather than duplicated, so there is one definition of
        // "these terms are coherent" and a rule added to Create cannot be forgotten here.
        var revalidated = Create(
            Code, description, discount, window, scope, stacking,
            minimumSpend, maxRedemptions, maxRedemptionsPerCustomer, nowUtc);

        if (revalidated.IsError)
        {
            return revalidated.Errors;
        }

        Description = description.Trim();
        Discount = discount;
        Window = window;
        Scope = scope;
        Stacking = stacking;
        MinimumSpend = minimumSpend;
        MaxRedemptions = maxRedemptions;
        MaxRedemptionsPerCustomer = maxRedemptionsPerCustomer;

        RaiseDomainEvent(new PromotionReconfiguredEvent(Id, Code.Value, nowUtc));

        return Result.Success;
    }

    // ---------------------------------------------------------------- lifecycle

    public ErrorOr<Success> Activate(DateTimeOffset nowUtc)
    {
        // Refusing to activate something already over is the direct answer to M16: Onyx shipped
        // SUMMER25 with an expiry in the past, and nothing anywhere could have noticed.
        if (Window.HasEndedAt(nowUtc))
        {
            return Error.Validation(
                "Promotion.WindowAlreadyEnded",
                "That promotion's validity window has already ended, so activating it would do "
                + "nothing. Change the window first.");
        }

        var status = Status.Activate();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        RaiseDomainEvent(new PromotionActivatedEvent(
            Id, Code.Value, Window.StartsAtUtc, Window.EndsAtUtc, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> Pause(DateTimeOffset nowUtc)
    {
        var status = Status.Pause();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        RaiseDomainEvent(new PromotionPausedEvent(Id, Code.Value, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> Close(PromotionCloseReason reason, DateTimeOffset nowUtc)
    {
        var status = Status.Close();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        RaiseDomainEvent(new PromotionClosedEvent(
            Id, Code.Value, reason, RedemptionCount, nowUtc));

        return Result.Success;
    }

    // ---------------------------------------------------------------- applying the code

    /// <summary>
    /// Whether this promotion can be used, and what it would be worth.
    ///
    /// <para>
    /// <b>The single definition of "usable", called by both the validation query and the
    /// reservation command.</b> That matters more than it looks: if the quote and the reservation
    /// each had their own version of the rules, a basket could be quoted a discount the
    /// reservation then refused — which is Onyx <b>M18</b> ("one rule written twice, as
    /// <c>&lt;=</c> and <c>&lt;</c>, so two screens disagreed") applied to money instead of
    /// stock.
    /// </para>
    ///
    /// <para>
    /// <paramref name="customerRedemptionsSoFar"/> is passed in rather than looked up, because
    /// the aggregate does not query. The caller counts the ledger; the aggregate decides what the
    /// count means.
    /// </para>
    /// </summary>
    public ErrorOr<Money> Evaluate(
        Money merchandiseSubtotal,
        Money scopedSubtotal,
        int customerRedemptionsSoFar,
        DateTimeOffset nowUtc)
    {
        // Paused and Draft are reported as "not recognised" rather than as their real state: a
        // customer cannot act on internal campaign state, and a code that is not yet live should
        // not be confirmable as existing before its start date.
        if (!Status.IsActive)
        {
            return PromotionErrors.CodeNotRecognised;
        }

        if (Window.HasNotStartedAt(nowUtc))
        {
            return PromotionErrors.NotStartedYet(Window.StartsAtUtc);
        }

        if (Window.HasEndedAt(nowUtc))
        {
            return PromotionErrors.Expired;
        }

        if (HasReachedTotalCap)
        {
            return PromotionErrors.UsageCapReached;
        }

        if (MaxRedemptionsPerCustomer is { } perCustomer
            && customerRedemptionsSoFar >= perCustomer)
        {
            return PromotionErrors.CustomerCapReached(perCustomer);
        }

        // The minimum is tested against the WHOLE merchandise subtotal even for a scoped
        // promotion. "Spend 100, get 20% off headphones" is the normal reading of a minimum
        // spend; testing it against only the scoped lines would make it a different offer.
        if (MinimumSpend is { } minimum)
        {
            if (minimum.CurrencyCode != merchandiseSubtotal.CurrencyCode)
            {
                return PromotionErrors.CurrencyMismatch(
                    minimum.CurrencyCode, merchandiseSubtotal.CurrencyCode);
            }

            if (merchandiseSubtotal.Amount < minimum.Amount)
            {
                return PromotionErrors.MinimumSpendNotMet(minimum.Amount, minimum.CurrencyCode);
            }
        }

        // The discount applies to the scoped lines, so a basket containing nothing in scope is
        // refused with a reason rather than quoted a discount of zero. Onyx's equivalent of
        // "quoted zero and said nothing" is M15 exactly.
        if (scopedSubtotal.IsZero)
        {
            return PromotionErrors.NotApplicableToBasket;
        }

        return Discount.ComputeFor(scopedSubtotal);
    }

    public bool HasReachedTotalCap =>
        MaxRedemptions is { } cap && RedemptionCount >= cap;

    /// <summary>
    /// Claims a use and returns the ledger row recording it.
    ///
    /// <para>
    /// The caller has already called <see cref="Evaluate"/>, and this re-checks the two caps
    /// anyway — because between a quote being shown and a checkout being confirmed, other people
    /// buy things. Re-checking here is what makes the window between quote and commit safe, and
    /// the concurrency token is what makes <i>this</i> check safe against a simultaneous one.
    /// </para>
    /// </summary>
    public ErrorOr<PromotionRedemption> Redeem(
        Guid customerId,
        string checkoutSessionId,
        int customerRedemptionsSoFar,
        DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(checkoutSessionId))
        {
            return PromotionErrors.ReferenceRequired;
        }

        if (!Status.IsActive)
        {
            return PromotionErrors.CodeNotRecognised;
        }

        if (!Window.Contains(nowUtc))
        {
            return Window.HasNotStartedAt(nowUtc)
                ? PromotionErrors.NotStartedYet(Window.StartsAtUtc)
                : PromotionErrors.Expired;
        }

        if (HasReachedTotalCap)
        {
            return PromotionErrors.UsageCapReached;
        }

        if (MaxRedemptionsPerCustomer is { } perCustomer
            && customerRedemptionsSoFar >= perCustomer)
        {
            return PromotionErrors.CustomerCapReached(perCustomer);
        }

        RedemptionCount++;

        RaiseDomainEvent(new PromotionRedeemedEvent(
            Id, Code.Value, customerId, checkoutSessionId, RedemptionCount, nowUtc));

        // Reaching the cap closes the campaign immediately, in the same transaction as the
        // redemption that filled it — so the next quote is refused by status rather than by a
        // cap check, and PromotionClosedIntegrationEvent tells reporting the moment it happened.
        if (HasReachedTotalCap)
        {
            Close(PromotionCloseReason.Exhausted, nowUtc);
        }

        return PromotionRedemption.Claim(
            Id, customerId, checkoutSessionId, customerRedemptionsSoFar + 1, nowUtc);
    }

    /// <summary>
    /// Gives a use back.
    ///
    /// <para>
    /// <b>This does not reopen a closed campaign</b>, and the asymmetry is deliberate. A
    /// "first 100 customers" promotion that closed on its hundredth sale should not quietly
    /// reopen because one of those customers abandoned their basket — the slot is returned to the
    /// count, so the campaign is not <i>permanently</i> short, but a human decides whether to
    /// run it again. Automatic reopening would make the campaign's end date unpredictable, and
    /// <c>Closed</c> is terminal for exactly that reason.
    /// </para>
    /// </summary>
    public void ReturnUse(
        Guid customerId,
        string checkoutSessionId,
        ReleaseReason reason,
        DateTimeOffset nowUtc)
    {
        if (RedemptionCount > 0)
        {
            RedemptionCount--;
        }

        RaiseDomainEvent(new PromotionReleasedEvent(
            Id, Code.Value, customerId, checkoutSessionId, reason, nowUtc));
    }

    /// <summary>
    /// The sum of the basket lines this promotion's scope matches, in the basket's currency.
    ///
    /// Lives on the aggregate because it is the scope's meaning, not the caller's: a scoped
    /// promotion discounts its own lines, and a caller computing the basis itself could get
    /// "20% off headphones" to take a fifth off a laptop.
    /// </summary>
    public Money ScopedSubtotal(
        IEnumerable<(Guid ProductId, decimal LineTotal)> lines,
        string currencyCode)
    {
        var total = lines
            .Where(line => Scope.Includes(line.ProductId))
            .Sum(line => line.LineTotal);

        var money = Money.Create(total, currencyCode);

        // A negative sum cannot be represented by Money and cannot arise from valid lines, so
        // treating the failure as zero is the safe reading: no scoped value means the promotion
        // does not apply, which Evaluate turns into NotApplicableToBasket.
        return money.IsError ? Money.Zero(currencyCode) : money.Value;
    }
}
