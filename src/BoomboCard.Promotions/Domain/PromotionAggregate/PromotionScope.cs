using BoomboCard.SharedKernel;

namespace BoomboCard.Promotions.Domain.PromotionAggregate;

public enum PromotionScopeKind
{
    /// <summary>Applies to the whole basket.</summary>
    EntireBasket = 0,

    /// <summary>
    /// Applies only to the named products, and the discount is computed against <i>their</i>
    /// lines rather than the whole subtotal.
    /// </summary>
    SpecificProducts = 1
}

/// <summary>
/// What a promotion applies to.
///
/// <para>
/// <b>Two kinds, not three.</b> Onyx's capability-gap list (<c>ISSUES.md</c> §7) names
/// "product/category scope" under Promotions — and category scope is deliberately not here,
/// because <c>Product</c> has no category. Catalog's capability list names categories as a gap
/// too, so building category scope now would mean either inventing a category concept in
/// Promotions that Catalog cannot answer questions about, or adding a property to <c>Product</c>
/// purely so this enum could have a third case.
/// </para>
///
/// <para>
/// That second option is how <c>CONCEPTS.md</c> §11 ended up documenting an
/// <c>IX_Products_CategoryId</c> index, complete with a code snippet, for a column that does not
/// exist. It was found while making this decision. Category scope arrives when Catalog has
/// categories, and the gap is recorded in <c>ROADMAP.md</c> rather than half-built.
/// </para>
///
/// <para>
/// <b>Why the scope changes the basis, not just the eligibility.</b> "20% off headphones" with a
/// basket of headphones and a laptop must discount the headphones, not a fifth of the laptop.
/// So a scoped promotion computes against the sum of its own matching lines. Onyx applied every
/// discount to the whole subtotal because the whole subtotal was the only number it had.
/// </para>
/// </summary>
public sealed class PromotionScope : ValueObject
{
    private readonly List<Guid> _productIds = [];

    public PromotionScopeKind Kind { get; private set; }

    public IReadOnlyCollection<Guid> ProductIds => _productIds.AsReadOnly();

    /// <summary>
    /// For EF, and the reason <see cref="Kind"/> has a private setter rather than being
    /// get-only.
    ///
    /// <para>
    /// EF cannot bind a constructor parameter to a primitive collection any more than it can to
    /// an owned navigation, so the two-argument constructor alone produced "no suitable
    /// constructor was found for the type 'PromotionScope'" at model-building time. The
    /// collection is mapped to the <c>_productIds</c> field by name instead, and the type stays
    /// immutable to every caller: the field is private, the property is a read-only view, and
    /// the two factories are the only public route in.
    /// </para>
    /// </summary>
    private PromotionScope() { }

    private PromotionScope(PromotionScopeKind kind, IEnumerable<Guid> productIds)
    {
        Kind = kind;
        _productIds = [.. productIds];
    }

    public static PromotionScope EntireBasket => new(PromotionScopeKind.EntireBasket, []);

    public static PromotionScope ForProducts(IEnumerable<Guid> productIds)
    {
        var ids = productIds.Where(id => id != Guid.Empty).Distinct().ToList();

        // An empty product list would mean "applies to nothing", which no merchandiser intends
        // and which would silently make the promotion worthless. Treated as the whole basket,
        // because that is the only other thing it could mean — and the create command rejects an
        // explicitly-empty list before reaching here.
        return ids.Count == 0
            ? EntireBasket
            : new PromotionScope(PromotionScopeKind.SpecificProducts, ids);
    }

    public bool Includes(Guid productId) =>
        Kind is PromotionScopeKind.EntireBasket || _productIds.Contains(productId);

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Kind;

        // Ordered, so two scopes over the same products in a different order are equal.
        foreach (var id in _productIds.Order())
        {
            yield return id;
        }
    }

    public override string ToString() =>
        Kind is PromotionScopeKind.EntireBasket
            ? "the entire basket"
            : $"{_productIds.Count} product(s)";
}

/// <summary>
/// Whether a promotion tolerates company.
///
/// <para>
/// <c>ISSUES.md</c> §7 names "stacking rules" as a missing capability, and this is the whole of
/// it: a promotion either combines with others or it does not. The rule is enforced at
/// <b>reservation</b> time, where the ledger can see which other promotions a checkout session
/// already holds — so it works regardless of how many codes the checkout UI chooses to accept.
/// </para>
///
/// <para>
/// <c>Exclusive</c> is the default on creation. A merchandiser who has not thought about
/// stacking has almost certainly not intended "combine this 30% code with every other 30% code",
/// and the failure direction matters: the strict default costs a conversation, the permissive one
/// costs margin.
/// </para>
/// </summary>
public enum StackingPolicy
{
    /// <summary>Cannot be used alongside any other promotion on the same checkout.</summary>
    Exclusive = 0,

    /// <summary>May be used alongside other promotions that also permit it.</summary>
    Combinable = 1
}
