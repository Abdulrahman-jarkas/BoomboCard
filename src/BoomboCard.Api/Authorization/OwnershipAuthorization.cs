using System.Security.Claims;
using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace BoomboCard.Api.Authorization;

/// <summary>
/// "You are the customer this route is about, or you are an administrator."
///
/// <para>
/// Onyx S4 is the finding this exists for, and it is worth stating precisely because the bug was
/// not a missing <c>[Authorize]</c>: authentication worked. Every customer-facing action took an
/// id from the route — <c>GetById(int id)</c>, <c>GetOrders(int id)</c>,
/// <c>Update(int id, ...)</c> — and never once compared it to the authenticated principal. Any
/// signed-in customer could read or edit any other customer's profile and order history by
/// changing a number in the URL. Horizontal privilege escalation on every endpoint, with a
/// perfectly good JWT attached to the request.
/// </para>
/// </summary>
public sealed class ResourceOwnerRequirement : IAuthorizationRequirement;

public sealed class ResourceOwnerHandler(IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<ResourceOwnerRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ResourceOwnerRequirement requirement)
    {
        if (context.User.IsInRole(nameof(CustomerRole.Admin)))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var routeCustomerId = httpContextAccessor.HttpContext?
            .Request.RouteValues[BoomboCardRouteValues.CustomerId]?.ToString();

        // Fails closed when the route has no customerId, when it is unparseable, or when the
        // principal has no customer id claim.
        //
        // This is the branch worth being deliberate about. The tempting alternative — "no route
        // id means this requirement does not apply, so succeed" — turns a route that forgot its
        // parameter, or spelled it `id` instead of `customerId`, into an endpoint with no
        // ownership check at all. That is Onyx S4 reintroduced by a typo, and it would pass
        // every test that only exercised the happy path. There is a test for this case.
        if (!Guid.TryParse(routeCustomerId, out var target) || target == Guid.Empty)
        {
            context.Fail(new AuthorizationFailureReason(
                this,
                $"The route does not carry a '{BoomboCardRouteValues.CustomerId}' value, so "
                + "ownership cannot be established."));

            return Task.CompletedTask;
        }

        var claim = context.User.FindFirstValue(BoomboCardClaims.CustomerId);

        if (Guid.TryParse(claim, out var principal) && principal == target)
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(
                this,
                "The authenticated customer does not own this resource."));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// "Authenticated, and the email address is verified." Separate from being signed in, because an
/// unverified account may manage itself but must not transact.
/// </summary>
public sealed class VerifiedEmailRequirement : IAuthorizationRequirement;

public sealed class VerifiedEmailHandler : AuthorizationHandler<VerifiedEmailRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        VerifiedEmailRequirement requirement)
    {
        // Read from the claim, so it costs no database round trip. The trade is staleness: a
        // customer who verifies mid-session carries an unverified token until it expires or they
        // sign in again. Acceptable because it fails in the safe direction — a verified customer
        // is briefly treated as unverified, never the reverse — and because the verification
        // flow is the moment they are most likely to sign in anyway.
        if (string.Equals(
                context.User.FindFirstValue(BoomboCardClaims.EmailVerified),
                "true",
                StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(
                this,
                "This operation requires a verified email address."));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Reads the acting customer off the authenticated principal, for the handlers that have to
/// attribute a change to a person.
/// </summary>
public sealed class HttpContextCurrentActor(IHttpContextAccessor httpContextAccessor) : ICurrentActor
{
    public Guid? CustomerId =>
        Guid.TryParse(
            httpContextAccessor.HttpContext?.User.FindFirstValue(BoomboCardClaims.CustomerId),
            out var customerId)
            ? customerId
            : null;

    public bool IsAdmin =>
        httpContextAccessor.HttpContext?.User.IsInRole(nameof(CustomerRole.Admin)) == true;
}
