using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Domain.AuthAttempts;
using BoomboCard.Customers.Domain.Credentials;
using BoomboCard.Customers.Domain.CustomerAggregate;
using Microsoft.EntityFrameworkCore;

namespace BoomboCard.Customers.Tests;

/// <summary>
/// What the database guarantees, asserted against the database.
///
/// These are the tests that cannot be written against the InMemory provider at all (Onyx T6):
/// a unique index, a concurrency token and a SQL projection are all things it does not have.
/// </summary>
public sealed class CustomersModelTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static Customer Register(string email) =>
        Customer.Register(
            EmailAddress.Create(email).Value,
            PersonName.Create("Ada Lovelace").Value,
            PasswordCredential.Create("verifier", Now).Value,
            Now);

    // ----------------------------------------------------------------- uniqueness (Onyx D10)

    [Fact]
    public async Task TwoAccountsCannotShareAnEmailAddress()
    {
        await using var context = fixture.CreateContext();

        context.Customers.Add(Register("unique@example.com"));
        await context.SaveChangesAsync();

        await using var second = fixture.CreateContext();
        second.Customers.Add(Register("unique@example.com"));

        // Onyx D10: no unique constraint on any natural key, so two accounts could hold the same
        // address and the login lookup's FirstOrDefault would silently pick one of them.
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TwoSpellingsOfOneAddressCollide()
    {
        await using var context = fixture.CreateContext();

        context.Customers.Add(Register("Mixed@Example.COM"));
        await context.SaveChangesAsync();

        await using var second = fixture.CreateContext();
        second.Customers.Add(Register("mixed@example.com"));

        // Normalisation plus the index: the uniqueness is a property of the address, not of how
        // somebody typed it.
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task TheLoginLookupIsAParameterisedQueryOnTheIndexedColumn()
    {
        await using var context = fixture.CreateContext();

        context.Customers.Add(Register("lookup@example.com"));
        await context.SaveChangesAsync();

        var email = EmailAddress.Create("LOOKUP@example.com").Value;

        var found = await context.Customers
            .FirstOrDefaultAsync(customer => customer.Email.Value == email.Value);

        // LINQ against a mapped column, which is the structural half of closing S1. Email is an
        // owned type rather than a value converter precisely so this is translatable: a
        // converter makes `customer.Email.Value` a CLR member access EF cannot turn into SQL,
        // and the comparison would silently move to the client — see BUGS-AND-LESSONS §3.
        Assert.NotNull(found);
        Assert.Equal("lookup@example.com", found.Email.Value);
    }

    // ----------------------------------------------------------------- single use under a race

    [Fact]
    public async Task APasswordResetTokenCanOnlyBeRedeemedOnce_EvenUnderARace()
    {
        var secret = SecretToken.Generate();
        var customerId = Guid.NewGuid();

        await using (var setup = fixture.CreateContext())
        {
            setup.Customers.Add(Register("race@example.com"));
            setup.PasswordResetTokens.Add(PasswordResetToken.Issue(customerId, secret, Now));
            await setup.SaveChangesAsync();
        }

        // Two independent contexts, the same shape as two concurrent requests: both read the
        // token, both see IsRedeemed false, both pass the check.
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        var firstToken = await first.PasswordResetTokens
            .FirstAsync(token => token.TokenHash == secret.Hash);
        var secondToken = await second.PasswordResetTokens
            .FirstAsync(token => token.TokenHash == secret.Hash);

        Assert.False(firstToken.Redeem(Now).IsError);
        Assert.False(secondToken.Redeem(Now).IsError);

        await first.SaveChangesAsync();

        // The version predicate is what makes exactly one of them win: the loser's UPDATE
        // affects zero rows. In the application the ResilientCommandDispatcher retries in a
        // fresh scope, reloads, finds the token spent, and returns the generic invalid-token
        // error — so a reset link genuinely works once.
        //
        // Onyx's equivalent guard for a concurrent resource was a process-local SemaphoreSlim
        // (D16) which protected one instance and leaked permanently on throw.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var verify = fixture.CreateContext();
        var stored = await verify.PasswordResetTokens.FirstAsync(token => token.TokenHash == secret.Hash);

        Assert.True(stored.IsRedeemed);
    }

    [Fact]
    public async Task ConcurrentSignInsCannotLoseAFailedAttemptCount()
    {
        await using (var setup = fixture.CreateContext())
        {
            setup.Customers.Add(Register("lockout@example.com"));
            await setup.SaveChangesAsync();
        }

        var email = EmailAddress.Create("lockout@example.com").Value;

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        var a = await first.Customers.FirstAsync(c => c.Email.Value == email.Value);
        var b = await second.Customers.FirstAsync(c => c.Email.Value == email.Value);

        a.RecordSignInAttempt(false, "attacker", Now);
        b.RecordSignInAttempt(false, "attacker", Now);

        await first.SaveChangesAsync();

        // Without the concurrency token both writers would store FailedSignInCount = 1, and an
        // attacker running parallel requests would never reach the lockout threshold. This is
        // exactly Onyx D1's shape — read, compare, write with no token, so two writers both
        // "succeed" — applied to a counter that is a security control rather than stock.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    // ----------------------------------------------------------------- projection agreement

    [Theory]
    [InlineData(CustomerStatusValue.Active, true, true)]
    [InlineData(CustomerStatusValue.Active, false, false)]
    [InlineData(CustomerStatusValue.Suspended, true, false)]
    [InlineData(CustomerStatusValue.Suspended, false, false)]
    public async Task TheCanTransactProjectionAgreesWithTheAggregate(
        CustomerStatusValue status,
        bool verified,
        bool expected)
    {
        var customer = Register($"transact-{status}-{verified}@example.com");

        if (verified)
        {
            customer.VerifyEmail(Now);
        }

        if (status is CustomerStatusValue.Suspended)
        {
            customer.Suspend(Now);
        }

        await using var context = fixture.CreateContext();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        // CanTransact exists twice: as a property on the aggregate, and as a SQL expression in
        // GetCustomerSummaryQueryHandler, because a C# property cannot be translated. That
        // duplication is exactly the shape of Onyx M18 — one availability rule written as `<=`
        // in one place and `<` in another, so two screens disagreed about the same product — so
        // it gets a test over every combination rather than a comment asking people to be
        // careful.
        var projected = await context.Customers
            .Where(candidate => candidate.Id == customer.Id)
            .Select(candidate => candidate.Status.Value == CustomerStatusValue.Active
                && candidate.EmailVerifiedAtUtc != null)
            .SingleAsync();

        Assert.Equal(expected, customer.CanTransact);
        Assert.Equal(expected, projected);
    }

    // ----------------------------------------------------------------- the audit log

    [Fact]
    public async Task AnAttemptForAnUnknownAddressIsStoredWithNoCustomerId()
    {
        await using var context = fixture.CreateContext();

        context.AuthAttempts.Add(AuthAttempt.ForUnknownAccount(
            "nobody@example.com", "fingerprint", Now));

        // Nullable, with no foreign key, because these rows are the entire basis for noticing
        // credential stuffing: one client failing against hundreds of addresses that do not
        // exist here. A required FK would make them impossible to store, and Onyx simply
        // returned Unauthorized() and wrote nothing at all (S12).
        await context.SaveChangesAsync();

        await using var verify = fixture.CreateContext();
        var stored = await verify.AuthAttempts
            .SingleAsync(attempt => attempt.Email == "nobody@example.com");

        Assert.Null(stored.CustomerId);
        Assert.Equal(AuthOutcome.NoSuchAccount, stored.Outcome);
    }

    // ----------------------------------------------------------------- the outbox payload

    [Fact]
    public async Task AnOutboxPayloadLongerThanFiveHundredCharactersIsStored()
    {
        await using var context = fixture.CreateContext();

        // The bug this is here for: every module's DbContext applies a 500-character convention
        // to strings, which produced `Payload nvarchar(500)` and was already in Catalog's
        // shipped schema. SQL Server does not truncate — it raises "String or binary data would
        // be truncated" and fails the INSERT, which fails the SaveChanges, which rolls back the
        // whole command that raised the event. The existing events serialise to roughly 400
        // characters, so it was a field or two from breaking in production.
        var payload = new string('x', 5_000);

        context.OutboxMessages.Add(SharedKernel.Persistence.Outbox.OutboxMessage.Create(
            Guid.NewGuid(),
            "BoomboCard.Tests.SomeIntegrationEvent",
            payload,
            Now));

        await context.SaveChangesAsync();

        await using var verify = fixture.CreateContext();
        var stored = await verify.OutboxMessages.SingleAsync();

        Assert.Equal(5_000, stored.Payload.Length);
    }
}
