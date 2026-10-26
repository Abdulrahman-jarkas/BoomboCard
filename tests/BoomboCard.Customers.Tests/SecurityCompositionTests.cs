using System.Security.Claims;
using BoomboCard.Api.Authentication;
using BoomboCard.Api.Authorization;
using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace BoomboCard.Customers.Tests;

/// <summary>
/// The host's security composition: the ownership check and the startup validation.
///
/// These are the two places where a mistake is silent rather than loud, so they are the two that
/// most need assertions rather than prose.
/// </summary>
public sealed class SecurityCompositionTests
{
    // ----------------------------------------------------------------- ownership (Onyx S4)

    private static ClaimsPrincipal Principal(Guid customerId, CustomerRole role = CustomerRole.Customer) =>
        new(new ClaimsIdentity(
            [
                new Claim(BoomboCardClaims.CustomerId, customerId.ToString()),
                new Claim(ClaimTypes.Role, role.ToString())
            ],
            authenticationType: "Test",
            nameType: ClaimTypes.Name,
            roleType: ClaimTypes.Role));

    private static (ResourceOwnerHandler Handler, AuthorizationHandlerContext Context) Arrange(
        ClaimsPrincipal user,
        string? routeCustomerId)
    {
        var http = new DefaultHttpContext { User = user };

        if (routeCustomerId is not null)
        {
            http.Request.RouteValues[BoomboCardRouteValues.CustomerId] = routeCustomerId;
        }

        var accessor = new HttpContextAccessor { HttpContext = http };
        var requirement = new ResourceOwnerRequirement();

        return (
            new ResourceOwnerHandler(accessor),
            new AuthorizationHandlerContext([requirement], user, resource: null));
    }

    [Fact]
    public async Task ACustomerMayReachTheirOwnResource()
    {
        var customerId = Guid.NewGuid();
        var (handler, context) = Arrange(Principal(customerId), customerId.ToString());

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task ACustomerMayNotReachSomebodyElsesResource()
    {
        var (handler, context) = Arrange(Principal(Guid.NewGuid()), Guid.NewGuid().ToString());

        // Onyx S4, in one assertion. Every customer-facing action there took an id from the
        // route and never compared it to the authenticated principal, so any signed-in customer
        // could read or edit any other customer's profile and order history by changing a number
        // in the URL — with authentication working perfectly.
        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task AnAdministratorMayReachAnybodysResource()
    {
        var (handler, context) = Arrange(
            Principal(Guid.NewGuid(), CustomerRole.Admin),
            Guid.NewGuid().ToString());

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task TheOwnershipCheckFailsClosedWhenTheRouteCarriesNoUsableId(string? routeValue)
    {
        var (handler, context) = Arrange(Principal(Guid.NewGuid()), routeValue);

        // The branch worth being deliberate about, and the reason this test exists.
        //
        // The tempting implementation is "no route id means this requirement does not apply, so
        // succeed" — which turns a route that forgot its parameter, or spelled it `id` instead
        // of `customerId`, into an endpoint with no ownership check at all. That is S4
        // reintroduced by a typo, and it would pass every test that only exercised the happy
        // path. So it fails, and so does this.
        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task AnUnauthenticatedPrincipalOwnsNothing()
    {
        var (handler, context) = Arrange(new ClaimsPrincipal(new ClaimsIdentity()), Guid.NewGuid().ToString());

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    // ----------------------------------------------------------------- verified email

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public async Task TheVerifiedEmailRequirementReadsTheClaimAndFailsClosed(
        string? claimValue,
        bool expected)
    {
        var claims = new List<Claim>();

        if (claimValue is not null)
        {
            claims.Add(new Claim(BoomboCardClaims.EmailVerified, claimValue));
        }

        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        var requirement = new VerifiedEmailRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);

        await new VerifiedEmailHandler().HandleAsync(context);

        Assert.Equal(expected, context.HasSucceeded);
    }

    // ----------------------------------------------------------------- startup validation

    [Theory]
    [InlineData("", "boombocard", "api")]
    [InlineData("too-short", "boombocard", "api")]
    [InlineData("a-long-enough-signing-key-of-32-bytes", "", "api")]
    [InlineData("a-long-enough-signing-key-of-32-bytes", "boombocard", "")]
    public void TheHostRefusesToStartWithoutUsableTokenSettings(
        string key,
        string issuer,
        string audience)
    {
        var options = new JwtOptions { Key = key, Issuer = issuer, Audience = audience };

        // Onyx read _configuration["Jwt:Key"]! inline in its login action with a null-forgiving
        // operator, so a missing key was a 500 on the first authentication attempt in production
        // rather than a refusal to start — and the key it read was committed to appsettings.json
        // alongside a live payment key and a production database password (S5). It then
        // validated neither issuer nor audience on the way back in (S15).
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void AValidConfigurationPasses()
    {
        var options = new JwtOptions
        {
            Key = "a-signing-key-that-is-at-least-thirty-two-bytes",
            Issuer = "boombocard",
            Audience = "boombocard-api"
        };

        options.Validate();
    }

    [Fact]
    public void TheCustomersModuleRefusesToStartWithoutAFingerprintKey()
    {
        // No default, on purpose. The IPv4 space is small enough to enumerate completely, so an
        // unkeyed hash of a client address is not anonymous at all — and a security control with
        // a convenient built-in default is one that is off in production without anyone noticing.
        var options = new Customers.Application.CustomersOptions();

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void TheFingerprintIsKeyed_SoTwoDeploymentsDoNotShareHashes()
    {
        var first = new Customers.Application.Auth.ClientFingerprint(
            new Customers.Application.CustomersOptions { ClientFingerprintKey = "key-one" });

        var second = new Customers.Application.Auth.ClientFingerprint(
            new Customers.Application.CustomersOptions { ClientFingerprintKey = "key-two" });

        Assert.NotEqual(first.Of("203.0.113.1"), second.Of("203.0.113.1"));

        // Deterministic within one key, which is what makes "is this the same client again"
        // answerable without retaining the address.
        Assert.Equal(first.Of("203.0.113.1"), first.Of("203.0.113.1"));
        Assert.NotEqual(first.Of("203.0.113.1"), first.Of("203.0.113.2"));
        Assert.Null(first.Of(null));
    }
}
