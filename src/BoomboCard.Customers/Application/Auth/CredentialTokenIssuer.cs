using BoomboCard.Customers.Contracts.IntegrationEvents;
using BoomboCard.Customers.Data;
using BoomboCard.Customers.Data.Repositories;
using BoomboCard.Customers.Domain.Credentials;
using BoomboCard.Customers.Domain.CustomerAggregate;
using BoomboCard.SharedKernel.Persistence.Outbox;

namespace BoomboCard.Customers.Application.Auth;

/// <summary>
/// Issues a single-use credential token: generates it, stores its hash, and enqueues the message
/// that carries the plaintext to the customer.
///
/// <para>
/// <b>Why these three steps are one method.</b> They have to happen together or not at all. A
/// token row with no message is a link nobody receives; a message with no row is a link that
/// fails when clicked; and the plaintext exists for exactly the duration of this call, so there
/// is no later point at which the message could be assembled. Both writes go into the current
/// transaction's change tracker and commit with the command that called it.
/// </para>
///
/// <para>
/// <b>Why not a domain event handler.</b> It would be the natural shape — <c>CustomerRegistered</c>
/// fires, something reacts, a verification email goes out — and it is wrong here, because the
/// plaintext token is a bearer credential. Putting it on a domain event hands it to every
/// current and future listener of that event, which is the opposite of a secret that exists
/// once. So the issuing is explicit at the one call site that needs it.
/// </para>
///
/// <para>
/// <b>Why the plaintext is in an outbox row at all.</b> It is the least bad option, and it is
/// bounded. Notifications cannot send a link it does not have; the alternative is Customers
/// calling a mail provider directly, inside a transaction, which is Onyx A12 (a transaction held
/// open across external HTTP) and A8 (fire-and-forget into a method that threw) at the same
/// time. The row lives for seconds in normal operation, and <c>OutboxMaintenance</c> deletes
/// published messages — so the window is short and the token expires regardless.
/// </para>
/// </summary>
internal interface ICredentialTokenIssuer
{
    void IssueEmailVerification(Customer customer, DateTimeOffset nowUtc);

    void IssuePasswordReset(Customer customer, DateTimeOffset nowUtc);
}

internal sealed class CredentialTokenIssuer(
    ICustomerRepository customers,
    IOutboxWriter<CustomersDbContext> outbox) : ICredentialTokenIssuer
{
    public void IssueEmailVerification(Customer customer, DateTimeOffset nowUtc)
    {
        var secret = SecretToken.Generate();
        var token = EmailVerificationToken.Issue(customer.Id, secret, nowUtc);

        customers.AddEmailVerificationToken(token);

        outbox.Enqueue(new EmailVerificationRequestedIntegrationEvent(
            Guid.NewGuid(),
            customer.Id,
            customer.Email.Value,
            secret.Plaintext,
            token.ExpiresAtUtc,
            nowUtc));
    }

    public void IssuePasswordReset(Customer customer, DateTimeOffset nowUtc)
    {
        var secret = SecretToken.Generate();
        var token = PasswordResetToken.Issue(customer.Id, secret, nowUtc);

        customers.AddPasswordResetToken(token);

        outbox.Enqueue(new PasswordResetRequestedIntegrationEvent(
            Guid.NewGuid(),
            customer.Id,
            customer.Email.Value,
            secret.Plaintext,
            token.ExpiresAtUtc,
            nowUtc));
    }
}
