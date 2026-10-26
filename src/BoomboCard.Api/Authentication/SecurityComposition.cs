using System.Text;
using System.Threading.RateLimiting;
using BoomboCard.Api.Authorization;
using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Contracts.Authorization;
using BoomboCard.Customers.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace BoomboCard.Api.Authentication;

/// <summary>
/// Authentication, authorization and rate limiting for the whole host, in one place.
///
/// These are deliberately the host's job rather than any module's: there is one authentication
/// scheme, one signing key and one set of limits for the process, and seven modules each
/// configuring their own would be seven chances to configure one differently. Modules name
/// policies (<see cref="BoomboCardPolicies"/>) and rate limits
/// (<see cref="CustomerRateLimits"/>); the host defines what those names mean.
/// </summary>
public static class SecurityComposition
{
    public static IServiceCollection AddBoomboCardSecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? new JwtOptions();

        // At startup. A missing or too-short key is a refusal to boot, not a 500 on the first
        // sign-in — see JwtOptions.Validate.
        jwt.Validate();

        services.AddSingleton(jwt);
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentActor, HttpContextCurrentActor>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // All five, explicitly. Onyx S15 set ValidateIssuer and ValidateAudience to
                    // false, so any token signed with the shared key was accepted whoever
                    // minted it and whoever it was for.
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,

                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,

                    ValidateLifetime = true,

                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),

                    // Default is five minutes, which silently extends every token's life. Zero
                    // means expiry means expiry.
                    ClockSkew = TimeSpan.Zero
                };
            });

        services.AddAuthorization(options =>
        {
            // Deny by default. A route that names no policy still requires an authenticated
            // principal, so forgetting one fails closed.
            //
            // This is the structural answer to Onyx S16 and S2. Onyx applied [Authorize] per
            // controller and per action, and missed one: DELETE /api/admin/customers/{id} had no
            // attribute, so it was reachable with no token at all — while the PR claimed
            // "all endpoints are properly authorized behind JWT and scoped to the Admin role,
            // consistent with the rest of the API" (X14). With a fallback policy that endpoint
            // would have required authentication by default and the omission would have been a
            // 401 in testing rather than a hole in production.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            options.AddPolicy(
                BoomboCardPolicies.Admin,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireRole(nameof(CustomerRole.Admin)));

            options.AddPolicy(
                BoomboCardPolicies.SelfOrAdmin,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ResourceOwnerRequirement()));

            options.AddPolicy(
                BoomboCardPolicies.VerifiedCustomer,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new VerifiedEmailRequirement()));
        });

        services.AddSingleton<IAuthorizationHandler, ResourceOwnerHandler>();
        services.AddSingleton<IAuthorizationHandler, VerifiedEmailHandler>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Partitioned by client address, because the thing being limited is a client's
            // ability to make guesses — a global limit would let one attacker lock every honest
            // user out, which is a worse outcome than the attack.
            //
            // Onyx S12: no rate limiting at all, on a login endpoint that also had an SQL
            // injection in it (S1) and no account lockout to fall back on.
            options.AddPolicy(CustomerRateLimits.Authentication, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        // Ten attempts a minute per client. Generous for a person signing in,
                        // and four orders of magnitude short of useful for an online guessing
                        // attack. The per-account lockout is the second, independent layer:
                        // this one bounds a client, that one bounds an account, and an attacker
                        // distributing across many addresses still hits the lockout.
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
        });

        return services;
    }
}
