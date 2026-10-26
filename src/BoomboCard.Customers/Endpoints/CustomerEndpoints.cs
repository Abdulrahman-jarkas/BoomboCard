using BoomboCard.Customers.Application.Auth;
using BoomboCard.Customers.Application.Customers.Commands;
using BoomboCard.Customers.Application.Customers.Queries;
using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Contracts.Authorization;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BoomboCard.Customers.Endpoints;

/// <summary>
/// The module owns its own routes. The host calls <c>MapCustomerEndpoints</c> and knows nothing
/// about what is inside.
///
/// Four rules every endpoint here follows:
///
///  1. <b>Commands go through <c>ICommandDispatcher</c>, queries through <c>ISender</c>.</b>
///     The dispatcher adds the concurrency retry in a fresh scope, which is what makes a
///     single-use token single-use under a race.
///  2. <b>Errors map to real status codes.</b> Onyx A23 returned
///     <c>Ok(new { error = "Customer not found" })</c> — HTTP 200 with a failure inside it.
///  3. <b>Authorization is explicit on every route.</b> The host sets a deny-by-default
///     fallback policy, so a route that names nothing still requires authentication; the three
///     routes that must be anonymous say <c>AllowAnonymous</c> out loud. Onyx applied
///     <c>[Authorize]</c> per action and missed one — <c>DELETE /api/admin/customers/{id}</c>
///     was anonymous (S2, S16) while the PR claimed otherwise (X14).
///  4. <b>No entity is reachable from a response.</b> Every handler projects into a response
///     record. Onyx returned <c>Ok(customer)</c> from three endpoints, hash included (S11).
///
/// <para>
/// <b>Why the customer-scoped routes carry an id in the path</b> rather than reading it purely
/// from the token. A route of <c>/api/customers/{customerId}/addresses</c> plus the
/// <c>SelfOrAdmin</c> policy gives one mechanism that serves both the customer and an
/// administrator acting on their behalf, and makes the ownership check a visible, testable thing
/// on every route rather than an implicit assumption. The risk of that shape is precisely Onyx
/// S4 — taking the id from the route and never comparing it to the principal — so the policy
/// fails closed, and there is a test for the case where the route parameter is missing.
/// </para>
/// </summary>
public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");
        var customers = app.MapGroup("/api/customers").WithTags("Customers");
        var admin = app.MapGroup("/api/customers").WithTags("Customer administration");

        MapAuthEndpoints(auth);
        MapSelfServiceEndpoints(customers);
        MapAdminEndpoints(admin);

        return app;
    }

    // ----------------------------------------------------------------- auth

    private static void MapAuthEndpoints(RouteGroupBuilder auth)
    {
        auth.MapPost("/register", async (
            RegisterCustomerCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);

            return result.Match(
                customerId => Results.Created(
                    $"/api/customers/{customerId}",
                    new RegisteredCustomerResponse(customerId)),
                ToProblem);
        })
        .AllowAnonymous()
        .RequireRateLimiting(CustomerRateLimits.Authentication)
        .WithName("RegisterCustomer")
        .WithSummary("Create an account. Always a plain customer, never an administrator.")
        .WithDescription(
            "The request carries an email address, a name and a password, and nothing else — "
            + "there is no role or status field to supply. Onyx S3 model-bound a whole Customer "
            + "entity here and copied every property over the row, so a shopper could send "
            + "`\"Role\": \"Admin\"`.\n\n"
            + "A verification email is sent. The account can sign in immediately but cannot "
            + "check out until the address is confirmed.\n\n"
            + "Rate limited. 409 if the address is already registered.")
        .Produces<RegisteredCustomerResponse>(StatusCodes.Status201Created)
        .WithWriteProblems();

        auth.MapPost("/sign-in", async (
            SignInRequest request,
            ICommandDispatcher dispatcher,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            // The client address comes from the connection, never from the request body. A
            // caller-supplied value is a caller-controlled one, and the whole point of the
            // fingerprint is to correlate attempts an attacker would rather were uncorrelated.
            var command = new AuthenticateCommand(
                request.Email,
                request.Password,
                http.Connection.RemoteIpAddress?.ToString());

            var result = await dispatcher.SendAsync(command, cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .AllowAnonymous()
        .RequireRateLimiting(CustomerRateLimits.Authentication)
        .WithName("SignIn")
        .WithSummary("Exchange an email address and password for an access token.")
        .WithDescription(
            "Every failure — wrong password, unknown address, suspended account — returns the "
            + "same 401 with the same body, so this endpoint cannot be used to discover which "
            + "addresses are registered. The distinction is recorded in the audit log instead.\n\n"
            + "A locked-out account is the one exception and returns 403, because a customer who "
            + "has mistyped their password five times needs to be told that waiting will fix it.\n\n"
            + "Five consecutive failures start a lockout that doubles per failure from one minute "
            + "to a thirty-minute cap, and is cleared by a successful sign-in or a password "
            + "reset. Every attempt, successful or not, is recorded. Onyx had none of this (S12).")
        .Produces<AuthenticatedCustomerResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        auth.MapPost("/password-reset-requests", async (
            RequestPasswordResetCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);

            // Accepted either way. See the description.
            return result.Match(_ => Results.Accepted(), ToProblem);
        })
        .AllowAnonymous()
        .RequireRateLimiting(CustomerRateLimits.Authentication)
        .WithName("RequestPasswordReset")
        .WithSummary("Ask for a password-reset link.")
        .WithDescription(
            "Returns 202 whether or not the address has an account, and whether or not the "
            + "address is even well-formed. A different response for a known address would make "
            + "this an account-enumeration oracle, and no honest caller needs the distinction.\n\n"
            + "The token is 256 bits from the OS CSPRNG, stored only as a SHA-256 hash, valid for "
            + "one hour, and single-use — enforced by the aggregate's concurrency token, so two "
            + "requests racing with the same token result in exactly one reset. Onyx's was eight "
            + "characters from `new Random()`, stored in plaintext, with no expiry (S14).")
        .Produces(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        auth.MapPost("/password-resets", async (
            ResetPasswordCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);
            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .AllowAnonymous()
        .RequireRateLimiting(CustomerRateLimits.Authentication)
        .WithName("ResetPassword")
        .WithSummary("Complete a password reset with the token from the email.")
        .WithDescription(
            "An invalid, expired and already-used token all return the same 400, so the endpoint "
            + "cannot be used to probe which tokens exist.\n\n"
            + "Succeeding clears any lockout: whoever holds a valid single-use token has "
            + "demonstrated control of the inbox, and keeping the lockout would only punish the "
            + "victim of the guessing attempt.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        auth.MapPost("/email-verifications", async (
            VerifyEmailCommand command,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(command, cancellationToken);
            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .AllowAnonymous()
        .RequireRateLimiting(CustomerRateLimits.Authentication)
        .WithName("VerifyEmail")
        .WithSummary("Confirm an email address with the token from the verification email.")
        .WithDescription(
            "Anonymous, because the link is clicked from an email client that holds no access "
            + "token — the token in the link IS the proof. This is what flips `canTransact` to "
            + "true, so Sales will refuse to open a checkout until it has happened.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();
    }

    // ----------------------------------------------------------------- the customer's own account

    private static void MapSelfServiceEndpoints(RouteGroupBuilder customers)
    {
        customers.MapGet("/{customerId:guid}", async (
            Guid customerId,
            ISender sender,
            CancellationToken cancellationToken) =>
        {
            var result = await sender.Send(new GetCustomerProfileQuery(customerId), cancellationToken);
            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .WithName("GetCustomerProfile")
        .WithSummary("The account, its verification state and its addresses.")
        .WithDescription(
            "Readable by the account's owner or an administrator, and nobody else. Onyx S4 took "
            + "the id from the route and never compared it to the authenticated principal, so "
            + "any signed-in customer could read any other customer's profile by changing a "
            + "number in the URL.\n\n"
            + "Projected into a response record in SQL, so the password verifier column is never "
            + "read, let alone serialised (S11).")
        .Produces<CustomerProfileResponse>()
        .WithReadProblems();

        customers.MapPatch("/{customerId:guid}", async (
            Guid customerId,
            UpdateProfileRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new UpdateProfileCommand(customerId, request.Name),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .WithName("UpdateCustomerProfile")
        .WithSummary("Change the name on the account.")
        .WithDescription(
            "The name, and nothing else. Role, status, credentials and email are each their own "
            + "operation with their own authorization — Onyx S3 was one endpoint that accepted "
            + "the whole entity and wrote every property it was given.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        customers.MapPost("/{customerId:guid}/password", async (
            Guid customerId,
            ChangePasswordRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new ChangePasswordCommand(customerId, request.CurrentPassword, request.NewPassword),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .RequireRateLimiting(CustomerRateLimits.Authentication)
        .WithName("ChangePassword")
        .WithSummary("Change the password, proving the current one.")
        .WithDescription(
            "The current password is required even though the caller is already authenticated, so "
            + "a stolen session token is not enough to lock the real owner out of their own "
            + "account.\n\n"
            + "Minimum twelve characters, maximum 256, and no composition rules — length is the "
            + "parameter that buys entropy, and required symbols measurably push people towards "
            + "`Password1!` and towards reuse.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound);

        customers.MapPost("/{customerId:guid}/email-verification-requests", async (
            Guid customerId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new ResendEmailVerificationCommand(customerId),
                cancellationToken);

            return result.Match(_ => Results.Accepted(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .RequireRateLimiting(CustomerRateLimits.Authentication)
        .WithName("ResendEmailVerification")
        .WithSummary("Send another verification email.")
        .WithDescription(
            "Outstanding links are deliberately not invalidated: a customer who clicks the first "
            + "email after asking for a second should still end up verified. A password reset is "
            + "privileged enough to invalidate on use; verification is not.")
        .Produces(StatusCodes.Status202Accepted)
        .WithWriteProblems();

        customers.MapGet("/{customerId:guid}/sign-in-attempts", async (
            Guid customerId,
            ISender sender,
            CancellationToken cancellationToken,
            [FromQuery] int limit = 50) =>
        {
            var result = await sender.Send(
                new GetAuthAttemptsQuery(customerId, limit),
                cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .WithName("GetSignInAttempts")
        .WithSummary("Recent sign-in attempts on this account, newest first.")
        .WithDescription(
            "Visible to the account's owner as well as to an administrator, on purpose: the "
            + "person best placed to notice forty failed attempts from somewhere they have never "
            + "been is the owner.\n\n"
            + "`clientFingerprint` is a keyed HMAC of the client address, not the address — the "
            + "question it answers is \"is this the same client again\", which does not require "
            + "retaining an identifier for everyone who has ever mistyped a password. `limit` is "
            + "capped at 200.")
        .Produces<List<AuthAttemptResponse>>()
        .WithReadProblems();

        MapAddressEndpoints(customers);
    }

    private static void MapAddressEndpoints(RouteGroupBuilder customers)
    {
        customers.MapPost("/{customerId:guid}/addresses", async (
            Guid customerId,
            AddressRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new AddAddressCommand(
                    customerId,
                    request.Label,
                    request.Line1,
                    request.Line2,
                    request.City,
                    request.PostalCode,
                    request.CountryCode,
                    request.MakeDefault),
                cancellationToken);

            return result.Match(
                addressId => Results.Created(
                    $"/api/customers/{customerId}/addresses/{addressId}",
                    new CreatedAddressResponse(addressId)),
                ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .WithName("AddAddress")
        .WithSummary("Add an address. The first one becomes the default automatically.")
        .WithDescription(
            "`countryCode` is validated against the countries this system sells into — the same "
            + "definition Catalog prices against and the checkout quote takes its tax rate from. "
            + "An address somewhere with no configured price or tax rate cannot be stored, so a "
            + "checkout cannot later discover it has nowhere to ship to.\n\n"
            + "At most ten addresses per customer.")
        .Produces<CreatedAddressResponse>(StatusCodes.Status201Created)
        .WithWriteProblems();

        customers.MapPut("/{customerId:guid}/addresses/{addressId:guid}", async (
            Guid customerId,
            Guid addressId,
            AddressRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new UpdateAddressCommand(
                    customerId,
                    addressId,
                    request.Label,
                    request.Line1,
                    request.Line2,
                    request.City,
                    request.PostalCode,
                    request.CountryCode),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .WithName("UpdateAddress")
        .WithSummary("Replace an address.")
        .WithDescription(
            "The address is looked up inside the customer's own collection, so an id belonging "
            + "to someone else is a 404 rather than an edit — the ownership rule holds one layer "
            + "in from the authorization policy as well as at the route.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        customers.MapDelete("/{customerId:guid}/addresses/{addressId:guid}", async (
            Guid customerId,
            Guid addressId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new RemoveAddressCommand(customerId, addressId),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .WithName("RemoveAddress")
        .WithSummary("Remove an address.")
        .WithDescription(
            "409 when removing the default while other addresses remain: silently promoting an "
            + "arbitrary one would change where the customer's next order ships without telling "
            + "them, so they pick the replacement first.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        customers.MapPut("/{customerId:guid}/default-address", async (
            Guid customerId,
            SetDefaultAddressRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new SetDefaultAddressCommand(customerId, request.AddressId),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.SelfOrAdmin)
        .WithName("SetDefaultAddress")
        .WithSummary("Choose which address is the default.")
        .WithDescription(
            "The default is one nullable id on the customer, not a flag per address, so \"at "
            + "most one default\" is a shape that cannot express the violation rather than a "
            + "constraint something has to check.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();
    }

    // ----------------------------------------------------------------- administration

    private static void MapAdminEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/", async (
            ISender sender,
            CancellationToken cancellationToken,
            [FromQuery] string? search = null,
            [FromQuery] Domain.CustomerAggregate.CustomerStatusValue? status = null,
            [FromQuery] CustomerRole? role = null,
            [FromQuery] bool? emailVerified = null,
            [FromQuery] string? cursor = null,
            [FromQuery] int limit = 50) =>
        {
            var result = await sender.Send(
                new SearchCustomersQuery(search, status, role, emailVerified, cursor, limit),
                cancellationToken);

            return result.Match(Results.Ok, ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("SearchCustomers")
        .WithSummary("Search customers. Keyset paged.")
        .WithDescription(
            "Keyset paged over email, which is unique and ordered, so no row can be seen twice "
            + "or skipped however much the table changes between requests. Onyx A27 used "
            + "`.Skip().Take()` with no `ORDER BY`, so its pages were whatever the engine "
            + "returned, and A28 left the page size unbounded.\n\n"
            + "`addressCount` is counted by the database. Onyx A26 ran one extra query per "
            + "customer from inside a `Select` over an already-materialised list. `limit` is "
            + "capped at 200.")
        .Produces<CustomerPage>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

        admin.MapPut("/{customerId:guid}/role", async (
            Guid customerId,
            ChangeRoleRequest request,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new ChangeCustomerRoleCommand(customerId, request.Role),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("ChangeCustomerRole")
        .WithSummary("Grant or revoke the administrator role.")
        .WithDescription(
            "Three independent things have to hold, and all three do: this policy requires an "
            + "administrator; the aggregate refuses a change where the actor is the subject, so "
            + "nobody can promote themselves even by calling it from a job or a future endpoint; "
            + "and the resulting fact is published with the actor attached, so the audit trail "
            + "records who did it.\n\n"
            + "Onyx S3 had none of them: the role was writable through an ordinary profile "
            + "update, and A40 means nothing recorded that it had happened. 403 when the caller "
            + "is the subject.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems()
        .ProducesProblem(StatusCodes.Status403Forbidden);

        admin.MapPost("/{customerId:guid}/suspension", async (
            Guid customerId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new SuspendCustomerCommand(customerId),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("SuspendCustomer")
        .WithSummary("Suspend an account. Reversible.")
        .WithDescription(
            "The account keeps its history and its credential but cannot sign in or transact. "
            + "A suspended account's sign-in attempts return the same generic 401 as a wrong "
            + "password, because \"this account exists but is suspended\" is information an "
            + "attacker can use and the owner already has.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        admin.MapDelete("/{customerId:guid}/suspension", async (
            Guid customerId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new ReinstateCustomerCommand(customerId),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("ReinstateCustomer")
        .WithSummary("Lift a suspension, clearing any lockout with it.")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();

        admin.MapPost("/{customerId:guid}/anonymisation", async (
            Guid customerId,
            ICommandDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var result = await dispatcher.SendAsync(
                new AnonymiseCustomerCommand(customerId),
                cancellationToken);

            return result.Match(_ => Results.NoContent(), ToProblem);
        })
        .RequireAuthorization(BoomboCardPolicies.Admin)
        .WithName("AnonymiseCustomer")
        .WithSummary("Erase a customer's personal data. NOT reversible.")
        .WithDescription(
            "The row survives and the financial record with it; the name, email, credential and "
            + "every address line are overwritten. Terminal by design — a system that can "
            + "restore the data it promised to delete has not deleted it.\n\n"
            + "An integration event is published so other modules erase their copies, which is "
            + "the half of an erasure request that a `DELETE` in this database cannot do.\n\n"
            + "This replaces Onyx's `DELETE /api/admin/customers/{id}`, which called "
            + "`Remove(customer)` on a row with orders and payments hanging off it (D13) — "
            + "orphaning or failing on the financial record while not actually erasing anything, "
            + "since the name had already been copied elsewhere — and which carried no "
            + "authorization attribute at all, so it was reachable anonymously (S2).")
        .Produces(StatusCodes.Status204NoContent)
        .WithWriteProblems();
    }

    // ----------------------------------------------------------------- OpenAPI metadata

    private static RouteHandlerBuilder WithWriteProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static RouteHandlerBuilder WithReadProblems(this RouteHandlerBuilder builder) =>
        builder
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

    // ----------------------------------------------------------------- error mapping

    /// <summary>
    /// One mapper, so every endpoint returns the same shape. Onyx returned 404 from some paths,
    /// 200-with-an-error-body from others (A23), and raw stack traces from a handler that was
    /// not guarded by <c>IsDevelopment</c> (S10).
    /// </summary>
    private static IResult ToProblem(List<Error> errors)
    {
        var first = errors[0];

        var statusCode = first.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Problem(
            title: first.Code,
            detail: first.Description,
            statusCode: statusCode,
            extensions: errors.Count > 1
                ? new Dictionary<string, object?>
                {
                    ["errors"] = errors.Select(error => new { error.Code, error.Description }).ToArray()
                }
                : null);
    }
}

/*
 * Request and response contracts.
 *
 * Explicit records, one per operation, carrying only what that operation may change — never an
 * entity. Onyx A24 defined DTOs in a Dtos.cs that nothing referenced and serialised entities
 * instead, and S3 model-bound one straight onto the stored row.
 */

/// <summary>The id of the account just created.</summary>
public sealed record RegisteredCustomerResponse(Guid CustomerId);

/// <summary>
/// Credentials for sign-in. The client address is NOT here — it is read from the connection, so
/// a caller cannot choose their own fingerprint.
/// </summary>
public sealed record SignInRequest(string Email, string Password);

/// <summary>The only profile field a customer may change here.</summary>
public sealed record UpdateProfileRequest(string Name);

/// <summary>The current password is required even for an authenticated caller.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>One address. <c>CountryCode</c> must be a country this system sells into.</summary>
public sealed record AddressRequest(
    string Label,
    string Line1,
    string? Line2,
    string City,
    string? PostalCode,
    string CountryCode,
    bool MakeDefault = false);

/// <summary>The id of the address just created.</summary>
public sealed record CreatedAddressResponse(Guid AddressId);

/// <summary>Which of the customer's addresses becomes the default.</summary>
public sealed record SetDefaultAddressRequest(Guid AddressId);

/// <summary>
/// The role to grant or revoke. Its own request on its own endpoint behind its own policy —
/// which is the entire difference from Onyx S3.
/// </summary>
public sealed record ChangeRoleRequest(CustomerRole Role);

/// <summary>
/// Rate-limiter policy names. Defined by the module because the module's endpoints name them,
/// configured by the host because the limits are deployment policy — the same split as the
/// authorization policies.
/// </summary>
public static class CustomerRateLimits
{
    /// <summary>
    /// Everything that takes or changes a credential: sign-in, registration, password change,
    /// reset request, reset, verification.
    ///
    /// Onyx S12: "no rate limiting, no account lockout, no audit of auth attempts" — so an
    /// unauthenticated attacker could issue password guesses as fast as the server would answer,
    /// against an endpoint that also had an SQL injection in it (S1).
    /// </summary>
    public const string Authentication = "auth";
}
