using BoomboCard.Customers.Application.Auth;
using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Data;
using BoomboCard.Customers.Data.Repositories;
using BoomboCard.Customers.Domain.Credentials;
using BoomboCard.Customers.Domain.CustomerAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BoomboCard.Customers.Application.Customers.Commands;

/// <summary>
/// Changes a signed-in customer's password, proving the old one first.
///
/// <para>
/// The current password is required even though the caller is authenticated. A stolen session
/// token should not be enough to lock the real owner out of their own account, and this is the
/// step that makes a token theft recoverable rather than terminal.
/// </para>
/// </summary>
public sealed record ChangePasswordCommand(
    Guid CustomerId,
    string CurrentPassword,
    string NewPassword) : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class ChangePasswordCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    IPasswordVerifier passwordVerifier,
    TimeProvider timeProvider)
    : IRequestHandler<ChangePasswordCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ChangePasswordCommand command,
        CancellationToken cancellationToken)
    {
        var policy = PasswordPolicy.Validate(command.NewPassword);

        if (policy.IsError)
        {
            return policy.Errors;
        }

        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        var verification = passwordVerifier.Verify(
            customer.Credential.Verifier,
            command.CurrentPassword);

        if (verification is PasswordVerificationOutcome.Failed)
        {
            return PasswordCredentialErrors.Incorrect;
        }

        var changed = customer.ChangePassword(
            passwordVerifier.Hash(command.NewPassword),
            viaReset: false,
            timeProvider.GetUtcNow());

        if (changed.IsError)
        {
            return changed.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// Starts a password reset: issues a single-use token and has it mailed.
///
/// <para>
/// <b>Always succeeds, from the caller's point of view.</b> The endpoint returns the same 202
/// whether the address has an account or not, because a different response would turn this into
/// an account-enumeration oracle — and unlike the registration endpoint, there is no honest user
/// who needs the distinction: someone who typed their own address correctly will get the email,
/// and someone who typed it wrongly is not helped by being told this system has never heard of
/// them.
/// </para>
///
/// <para>
/// The work is therefore conditional and the response is not, which is the opposite way round
/// from how it reads. There is a test asserting the response for an unknown address is
/// byte-identical to the response for a known one.
/// </para>
/// </summary>
public sealed record RequestPasswordResetCommand(string Email)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class RequestPasswordResetCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    ICredentialTokenIssuer tokens,
    TimeProvider timeProvider,
    ILogger<RequestPasswordResetCommandHandler> logger)
    : IRequestHandler<RequestPasswordResetCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RequestPasswordResetCommand command,
        CancellationToken cancellationToken)
    {
        var email = EmailAddress.Create(command.Email);

        if (email.IsError)
        {
            // Not even a validation error back. A malformed address cannot have an account, and
            // saying "that is not a valid email address" is a response an attacker can tell
            // apart from the success case — which is the exact distinction this command exists
            // to deny them.
            return Result.Success;
        }

        var customer = await customers.GetByEmailAsync(email.Value, cancellationToken);

        // An anonymised or suspended account gets no reset link: there is nothing to reset to,
        // and a reset is not the route back from a suspension.
        if (customer is null || !customer.Status.IsActive)
        {
            logger.LogInformation(
                "Password reset requested for {Email}, which has no resettable account.",
                email.Value.Redacted);

            return Result.Success;
        }

        tokens.IssuePasswordReset(customer, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// Completes a password reset by redeeming a token.
/// </summary>
public sealed record ResetPasswordCommand(string Token, string NewPassword)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class ResetPasswordCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    IPasswordVerifier passwordVerifier,
    TimeProvider timeProvider)
    : IRequestHandler<ResetPasswordCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ResetPasswordCommand command,
        CancellationToken cancellationToken)
    {
        var policy = PasswordPolicy.Validate(command.NewPassword);

        if (policy.IsError)
        {
            return policy.Errors;
        }

        if (string.IsNullOrWhiteSpace(command.Token))
        {
            return PasswordResetErrors.Invalid;
        }

        // Looked up by hash, so the comparison is an indexed equality match inside the database
        // rather than a string compare in application code. Nothing here ever holds the stored
        // secret alongside the presented one, so there is no comparison for someone to later
        // "simplify" into a non-constant-time `==` — which is what Onyx's HashHelper did (S7).
        var nowUtc = timeProvider.GetUtcNow();

        var token = await customers.GetPasswordResetByHashAsync(
            SecretToken.HashOf(command.Token),
            cancellationToken);

        if (token is null)
        {
            return PasswordResetErrors.Invalid;
        }

        // Redeem first. If this loses the concurrency race against a second request presenting
        // the same token, the whole command is retried in a fresh scope and the reload finds the
        // token spent — so a reset link genuinely works once. See PasswordResetToken.Redeem.
        var redeemed = token.Redeem(nowUtc);

        if (redeemed.IsError)
        {
            return redeemed.Errors;
        }

        var customer = await customers.GetByIdAsync(token.CustomerId, cancellationToken);

        if (customer is null)
        {
            return PasswordResetErrors.Invalid;
        }

        var changed = customer.ChangePassword(
            passwordVerifier.Hash(command.NewPassword),
            viaReset: true,
            nowUtc);

        if (changed.IsError)
        {
            return changed.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}
