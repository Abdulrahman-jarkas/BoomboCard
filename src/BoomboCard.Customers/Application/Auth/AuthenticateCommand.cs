using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Data;
using BoomboCard.Customers.Data.Repositories;
using BoomboCard.Customers.Domain.AuthAttempts;
using BoomboCard.Customers.Domain.CustomerAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Customers.Application.Auth;

/// <summary>
/// What a successful sign-in returns. No role, no flags beyond the one the client needs to show
/// a "verify your email" prompt — everything else the client needs is in the token, and
/// everything the server needs it re-reads from the token on the next request.
/// </summary>
public sealed record AuthenticatedCustomerResponse(
    Guid CustomerId,
    string Token,
    DateTimeOffset ExpiresAtUtc,
    bool EmailVerified);

/// <summary>
/// Signs a customer in.
///
/// <para>
/// A <b>command</b>, not a query, and that is not a technicality: it writes. It records an
/// <c>AuthAttempt</c>, it increments or clears the failed-attempt counter, it may set a lockout,
/// and it may transparently upgrade the stored password hash. All of that has to be inside one
/// transaction with the decision it describes.
/// </para>
///
/// <para>
/// <see cref="ClientAddress"/> is supplied by the endpoint from the connection, never by the
/// caller, and is immediately hashed — see <c>ClientFingerprint</c>.
/// </para>
///
/// <para>
/// <b>This one endpoint carried six Onyx findings.</b> S1, unparameterised SQL built from the
/// submitted email, unauthenticated, against the table holding every password hash. S7, unsalted
/// single-pass SHA-256 compared with <c>==</c>. S12, no rate limit, no lockout, no audit. S15,
/// a token whose issuer and audience were never validated. S5, the signing key committed to
/// <c>appsettings.json</c>. X15, a PR note saying the raw SQL had been "verified … for the
/// accounts I tested with".
/// </para>
/// </summary>
public sealed record AuthenticateCommand(string Email, string Password, string? ClientAddress)
    : IRequest<ErrorOr<AuthenticatedCustomerResponse>>, ICustomersRequest;

internal sealed class AuthenticateCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    IPasswordVerifier passwordVerifier,
    IAccessTokenIssuer tokenIssuer,
    ClientFingerprint fingerprints,
    TimeProvider timeProvider,
    ILogger<AuthenticateCommandHandler> logger)
    : IRequestHandler<AuthenticateCommand, ErrorOr<AuthenticatedCustomerResponse>>
{
    public async Task<ErrorOr<AuthenticatedCustomerResponse>> Handle(
        AuthenticateCommand command,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var fingerprint = fingerprints.Of(command.ClientAddress);

        var email = EmailAddress.Create(command.Email);

        if (email.IsError)
        {
            // A malformed address cannot match an account, so there is nothing to attribute the
            // attempt to — but the generic failure is still what goes back, because
            // "that is not a valid email address" told to an unauthenticated caller is one more
            // signal about what this system will and will not accept.
            return PasswordCredentialErrors.Incorrect;
        }

        var customer = await customers.GetByEmailAsync(email.Value, cancellationToken);

        if (customer is null)
        {
            // Recorded, with no customer id. This row is the entire basis for noticing
            // credential stuffing: one client failing against hundreds of addresses that do not
            // exist here. Onyx returned Unauthorized() and wrote nothing (S12), so the campaign
            // would have been invisible during and after.
            customers.AddAuthAttempt(AuthAttempt.ForUnknownAccount(
                email.Value.Value, fingerprint, nowUtc));

            await unitOfWork.SaveChangesAsync(cancellationToken);

            LogOutcome(AuthOutcome.NoSuchAccount, email.Value, fingerprint);

            return PasswordCredentialErrors.Incorrect;
        }

        // The hash comparison happens here, not in the aggregate: it must be constant-time and
        // it has to understand the stored format. The aggregate is told the answer and decides
        // what it means.
        var verification = passwordVerifier.Verify(customer.Credential.Verifier, command.Password);

        var attempt = customer.RecordSignInAttempt(
            verification is not PasswordVerificationOutcome.Failed,
            fingerprint,
            nowUtc);

        customers.AddAuthAttempt(attempt);

        if (!attempt.Succeeded)
        {
            // Saved before returning the failure. The whole point of the attempt log is that it
            // records failures, so the failing path must be the one that cannot skip the write.
            await unitOfWork.SaveChangesAsync(cancellationToken);

            LogOutcome(attempt.Outcome, email.Value, fingerprint);

            return AuthOutcomeErrors.For(attempt.Outcome);
        }

        // Correct password, outdated hash format or work factor: re-hash and store it. This is
        // what lets the KDF cost rise with hardware without a migration or a forced reset.
        if (verification is PasswordVerificationOutcome.SucceededNeedsRehash)
        {
            customer.UpgradeCredential(passwordVerifier.Hash(command.Password));

            logger.LogInformation(
                "Upgraded the stored password hash for {CustomerId} on sign-in.",
                customer.Id);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Minted after the save, so the token only ever exists for a sign-in that is committed.
        // Issuing it first would hand out a credential for a transaction that might still roll
        // back — the same ordering mistake as Onyx A9, which dispatched a payment notification
        // before the payment row was added.
        var token = tokenIssuer.Issue(
            customer.Id,
            customer.Email.Value,
            customer.Role,
            customer.IsEmailVerified,
            nowUtc);

        logger.LogInformation(
            "Sign-in succeeded for {CustomerId} from {ClientFingerprint}.",
            customer.Id,
            fingerprint);

        return new AuthenticatedCustomerResponse(
            customer.Id,
            token.Token,
            token.ExpiresAtUtc,
            customer.IsEmailVerified);
    }

    /// <summary>
    /// Message template, never interpolation, and the redacted address rather than the real one.
    ///
    /// Onyx A42 wrote
    /// <c>_logger.LogInformation($"Fetched customer profile: {customer.Email}, credit limit …")</c>
    /// — an interpolated string, so the placeholders were baked into the message and structured
    /// logging was bypassed entirely, and a full email address plus a financial detail went into
    /// plaintext logs on every profile read.
    /// </summary>
    private void LogOutcome(AuthOutcome outcome, EmailAddress email, string? fingerprint) =>
        logger.LogInformation(
            "Sign-in failed with {Outcome} for {Email} from {ClientFingerprint}.",
            outcome,
            email.Redacted,
            fingerprint);
}
