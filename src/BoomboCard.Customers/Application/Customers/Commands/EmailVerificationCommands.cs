using BoomboCard.Customers.Application.Auth;
using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Data;
using BoomboCard.Customers.Data.Repositories;
using BoomboCard.Customers.Domain.Credentials;
using BoomboCard.Customers.Domain.CustomerAggregate;
using BoomboCard.SharedKernel;
using ErrorOr;
using MediatR;

namespace BoomboCard.Customers.Application.Customers.Commands;

/// <summary>
/// Confirms an email address by redeeming a verification token.
///
/// <para>
/// This is what flips <c>CanTransact</c> to true, which is why it matters beyond hygiene: until
/// it happens, Sales will refuse to open a checkout. Onyx had no verification at all, and fired
/// order confirmations at addresses nobody had ever shown could receive mail — from a method
/// whose body was <c>throw new NotImplementedException()</c>, discarded with
/// <c>_ = Task.Run(...)</c> (A8).
/// </para>
/// </summary>
public sealed record VerifyEmailCommand(string Token)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class VerifyEmailCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    TimeProvider timeProvider)
    : IRequestHandler<VerifyEmailCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        VerifyEmailCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Token))
        {
            return EmailVerificationErrors.Invalid;
        }

        var nowUtc = timeProvider.GetUtcNow();

        var token = await customers.GetEmailVerificationByHashAsync(
            SecretToken.HashOf(command.Token),
            cancellationToken);

        if (token is null)
        {
            return EmailVerificationErrors.Invalid;
        }

        var redeemed = token.Redeem(nowUtc);

        if (redeemed.IsError)
        {
            return redeemed.Errors;
        }

        var customer = await customers.GetByIdAsync(token.CustomerId, cancellationToken);

        if (customer is null)
        {
            return EmailVerificationErrors.Invalid;
        }

        var verified = customer.VerifyEmail(nowUtc);

        if (verified.IsError)
        {
            return verified.Errors;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}

/// <summary>
/// Issues a fresh verification link for the signed-in customer.
///
/// <para>
/// Needed because the first one expires after seven days, and because mail gets lost. Old
/// outstanding tokens are deliberately <b>not</b> invalidated: a customer who clicks the link
/// from the first email after requesting a second one should still end up verified, and
/// verification is not a privileged enough operation to be worth the confusion of
/// one-link-at-a-time. A password reset is, and that one does invalidate on use.
/// </para>
/// </summary>
public sealed record ResendEmailVerificationCommand(Guid CustomerId)
    : IRequest<ErrorOr<Success>>, ICustomersRequest;

internal sealed class ResendEmailVerificationCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    ICredentialTokenIssuer tokens,
    TimeProvider timeProvider)
    : IRequestHandler<ResendEmailVerificationCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ResendEmailVerificationCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);

        if (customer is null)
        {
            return CustomerErrors.NotFound(command.CustomerId);
        }

        if (customer.IsEmailVerified)
        {
            return CustomerErrors.EmailAlreadyVerified;
        }

        if (!customer.Status.IsActive)
        {
            return CustomerStatusErrors.Anonymised;
        }

        tokens.IssueEmailVerification(customer, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success;
    }
}
