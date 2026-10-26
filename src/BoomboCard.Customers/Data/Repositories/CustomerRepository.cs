using BoomboCard.Customers.Domain.AuthAttempts;
using BoomboCard.Customers.Domain.Credentials;
using BoomboCard.Customers.Domain.CustomerAggregate;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Customers.Data.Repositories;

internal sealed class CustomerRepository(CustomersDbContext context) : ICustomerRepository
{
    public void Add(Customer customer) => context.Customers.Add(customer);

    public void AddAuthAttempt(AuthAttempt attempt) => context.AuthAttempts.Add(attempt);

    public void AddPasswordResetToken(PasswordResetToken token) =>
        context.PasswordResetTokens.Add(token);

    public void AddEmailVerificationToken(EmailVerificationToken token) =>
        context.EmailVerificationTokens.Add(token);

    public Task<Customer?> GetByIdAsync(
        Guid customerId,
        CancellationToken cancellationToken = default) =>
        context.Customers
            .Include(customer => customer.Addresses)
            .FirstOrDefaultAsync(customer => customer.Id == customerId, cancellationToken);

    /// <summary>
    /// Tracked, and without the addresses: the sign-in path mutates the credential and nothing
    /// else, so loading an address collection it will not touch would be waste on the hottest
    /// and most attackable endpoint in the system.
    /// </summary>
    public Task<Customer?> GetByEmailAsync(
        EmailAddress email,
        CancellationToken cancellationToken = default) =>
        context.Customers
            .FirstOrDefaultAsync(customer => customer.Email.Value == email.Value, cancellationToken);

    public Task<bool> EmailExistsAsync(
        EmailAddress email,
        CancellationToken cancellationToken = default) =>
        context.Customers
            .AnyAsync(customer => customer.Email.Value == email.Value, cancellationToken);

    public Task<PasswordResetToken?> GetPasswordResetByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default) =>
        context.PasswordResetTokens
            .FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

    public Task<EmailVerificationToken?> GetEmailVerificationByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default) =>
        context.EmailVerificationTokens
            .FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);
}
