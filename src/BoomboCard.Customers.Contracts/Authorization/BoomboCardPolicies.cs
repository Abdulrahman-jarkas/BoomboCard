namespace BoomboCard.Customers.Contracts.Authorization;

/// <summary>
/// The authorization vocabulary, as names both the host and every module can see.
///
/// <para>
/// The host owns the authentication scheme, the signing key and the policy <i>implementations</i>;
/// a module's endpoint only ever names a policy. That split keeps JWT configuration out of seven
/// modules, and it is why these are strings here rather than types.
/// </para>
///
/// <para>
/// They live in Customers.Contracts because identity is the Customers module's subject. Any
/// module that needs to authorise a request already has a reason to reference it.
/// </para>
/// </summary>
public static class BoomboCardPolicies
{
    /// <summary>
    /// Administrative operations. Onyx X14 claimed "all endpoints are properly authorized behind
    /// JWT and scoped to the Admin role, consistent with the rest of the API" while
    /// <c>DELETE /api/admin/customers/{id}</c> carried no attribute at all and was reachable
    /// anonymously (S2).
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// The caller is the customer named by the route's <c>customerId</c>, or an administrator.
    ///
    /// <para>
    /// Onyx S4: every customer-facing endpoint took an id from the route and never compared it
    /// to the authenticated principal, so any logged-in customer could read any other customer's
    /// profile and order history by changing a number in the URL. Horizontal privilege
    /// escalation on every endpoint, with authentication working perfectly.
    /// </para>
    /// </summary>
    public const string SelfOrAdmin = "SelfOrAdmin";

    /// <summary>
    /// Authenticated, and the email address has been verified. Separate from being logged in,
    /// because an unverified account may manage itself but must not transact — otherwise an
    /// address typo becomes an order nobody can be told about.
    /// </summary>
    public const string VerifiedCustomer = "VerifiedCustomer";
}

/// <summary>
/// Claim types this system issues and reads. Named rather than repeated as string literals at
/// each use site, so the token and the policy that reads it cannot drift apart.
/// </summary>
public static class BoomboCardClaims
{
    public const string CustomerId = "customer_id";

    public const string EmailVerified = "email_verified";
}

/// <summary>
/// The route parameter <see cref="BoomboCardPolicies.SelfOrAdmin"/> compares against.
///
/// A customer-scoped route must spell it exactly this way. The policy <b>fails closed</b> when
/// the value is absent or unparseable rather than passing, because the alternative — an
/// ownership check that quietly does nothing on a route that forgot the parameter — is Onyx S4
/// with extra steps. There is a test for the absent case.
/// </summary>
public static class BoomboCardRouteValues
{
    public const string CustomerId = "customerId";
}
