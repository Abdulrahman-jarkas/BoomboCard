using BoomboCard.Customers.Domain.AuthAttempts;
using BoomboCard.Customers.Domain.Credentials;
using BoomboCard.Customers.Domain.CustomerAggregate;

namespace BoomboCard.Customers.Data.Repositories;

/// <summary>
/// Note the absence of <c>SaveChangesAsync</c>. Persisting is the unit of work's job — Onyx D3
/// committed order creation N+2 separate times while <c>ARCHITECTURE.md</c> claimed "a single
/// logical transaction … call SaveChanges() once at the end" (X1).
///
/// <para>
/// Note also the absence of raw SQL. Every method here is LINQ against a mapped, indexed
/// column, which is the structural half of closing Onyx S1 —
/// <c>$"SELECT * FROM Customers WHERE Email = '{email}' COLLATE NOCASE"</c> on the
/// unauthenticated login path. The other half is <see cref="EmailAddress"/> normalising, so
/// nobody needs a collation trick to get case-insensitive matching and reaches for string
/// concatenation to get one.
/// </para>
/// </summary>
internal interface ICustomerRepository
{
    void Add(Customer customer);

    void AddAuthAttempt(AuthAttempt attempt);

    void AddPasswordResetToken(PasswordResetToken token);

    void AddEmailVerificationToken(EmailVerificationToken token);

    /// <summary>
    /// Loads the aggregate with its addresses, tracked, for a command to mutate.
    /// </summary>
    Task<Customer?> GetByIdAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The login lookup. A parameterised equality match on <c>UX_Customers_Email</c>.
    /// </summary>
    Task<Customer?> GetByEmailAsync(
        EmailAddress email,
        CancellationToken cancellationToken = default);

    Task<bool> EmailExistsAsync(EmailAddress email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a reset token by the hash of the presented plaintext, so the comparison happens in
    /// the database on an indexed column rather than in application code.
    /// </summary>
    Task<PasswordResetToken?> GetPasswordResetByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);

    Task<EmailVerificationToken?> GetEmailVerificationByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);
}
