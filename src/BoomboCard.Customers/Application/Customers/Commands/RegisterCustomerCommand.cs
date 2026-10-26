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
/// Registers a new customer.
///
/// <para>
/// There is no <c>Role</c> parameter and no <c>Status</c> parameter, and that absence is the
/// design. Onyx S3 was not a missing authorization check — it was an endpoint whose request
/// shape could express "make me an administrator". A request contract that cannot say it cannot
/// be made to say it, whatever an authorization policy does or does not do.
/// </para>
///
/// <para>
/// The account is created Active but unverified, so it can sign in and manage itself while
/// <c>CanTransact</c> stays false until the address is confirmed.
/// </para>
/// </summary>
public sealed record RegisterCustomerCommand(string Email, string Name, string Password)
    : IRequest<ErrorOr<Guid>>, ICustomersRequest;

internal sealed class RegisterCustomerCommandHandler(
    ICustomerRepository customers,
    IUnitOfWork<CustomersDbContext> unitOfWork,
    IPasswordVerifier passwordVerifier,
    ICredentialTokenIssuer tokens,
    TimeProvider timeProvider)
    : IRequestHandler<RegisterCustomerCommand, ErrorOr<Guid>>
{
    public async Task<ErrorOr<Guid>> Handle(
        RegisterCustomerCommand command,
        CancellationToken cancellationToken)
    {
        var email = EmailAddress.Create(command.Email);

        if (email.IsError)
        {
            return email.Errors;
        }

        var name = PersonName.Create(command.Name);

        if (name.IsError)
        {
            return name.Errors;
        }

        var password = PasswordPolicy.Validate(command.Password);

        if (password.IsError)
        {
            return password.Errors;
        }

        // A read, so two simultaneous registrations for the same address can both pass it. The
        // unique index UX_Customers_Email is what actually guarantees one account per address;
        // this check exists to turn the common case into a 409 with a useful message rather than
        // a duplicate-key exception. Same division of labour as everywhere else here: the
        // constraint is the guarantee, the query is the courtesy.
        if (await customers.EmailExistsAsync(email.Value, cancellationToken))
        {
            return CustomerErrors.EmailAlreadyRegistered;
        }

        var nowUtc = timeProvider.GetUtcNow();

        var credential = PasswordCredential.Create(
            passwordVerifier.Hash(command.Password),
            nowUtc);

        if (credential.IsError)
        {
            return credential.Errors;
        }

        var customer = Customer.Register(email.Value, name.Value, credential.Value, nowUtc);

        customers.Add(customer);

        // Issued here rather than from a handler reacting to CustomerRegisteredEvent: the
        // plaintext is a bearer credential, and a domain event carrying one hands it to every
        // present and future listener. See ICredentialTokenIssuer. Both writes land in this
        // same transaction.
        tokens.IssueEmailVerification(customer, nowUtc);

        // One SaveChanges for the customer, the verification token and the outbox messages.
        // Onyx registered nobody — it had no sign-up at all — but its order creation is the
        // shape being avoided: N+2 commits, so a failure partway left half a result standing
        // (D3, X1).
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return customer.Id;
    }
}
