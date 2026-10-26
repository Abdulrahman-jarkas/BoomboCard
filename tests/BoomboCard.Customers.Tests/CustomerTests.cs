using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Domain.AuthAttempts;
using BoomboCard.Customers.Domain.CustomerAggregate;
using BoomboCard.Customers.Domain.CustomerAggregate.Events;
using BoomboCard.SharedKernel;

namespace BoomboCard.Customers.Tests;

/// <summary>
/// The <see cref="Customer"/> aggregate's invariants. Pure unit tests — no database, no
/// container.
/// </summary>
public sealed class CustomerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static Customer Register(string email = "ada@example.com") =>
        Customer.Register(
            EmailAddress.Create(email).Value,
            PersonName.Create("Ada Lovelace").Value,
            PasswordCredential.Create("verifier", Now).Value,
            Now);

    // ----------------------------------------------------------------- registration

    [Fact]
    public void Register_AlwaysProducesAPlainCustomer()
    {
        // There is no parameter for this, which is the point. Onyx S3 was an endpoint whose
        // request shape could say "make me an administrator"; a factory with no role parameter
        // cannot be asked.
        var customer = Register();

        Assert.Equal(CustomerRole.Customer, customer.Role);
        Assert.True(customer.Status.IsActive);
    }

    [Fact]
    public void Register_LeavesTheEmailUnverified_SoTheCustomerCannotYetTransact()
    {
        var customer = Register();

        Assert.False(customer.IsEmailVerified);
        Assert.False(customer.CanTransact);
    }

    [Fact]
    public void VerifyEmail_IsWhatMakesACustomerAbleToTransact()
    {
        var customer = Register();

        Assert.True(customer.VerifyEmail(Now).IsError is false);
        Assert.True(customer.CanTransact);

        // Twice is a conflict, not a silent no-op: a redeemed token reaching here again means
        // something upstream let a single-use credential through twice.
        Assert.True(customer.VerifyEmail(Now).IsError);
    }

    // ----------------------------------------------------------------- roles (Onyx S3)

    [Fact]
    public void ChangeRole_RefusesWhenTheActorIsTheSubject()
    {
        var customer = Register();

        var result = customer.ChangeRole(CustomerRole.Admin, customer.Id, Now);

        Assert.True(result.IsError);
        Assert.Equal("Customer.CannotChangeOwnRole", result.FirstError.Code);
        Assert.Equal(CustomerRole.Customer, customer.Role);
    }

    [Fact]
    public void ChangeRole_RefusesWithoutAnActor()
    {
        var customer = Register();

        Assert.True(customer.ChangeRole(CustomerRole.Admin, Guid.Empty, Now).IsError);
        Assert.Equal(CustomerRole.Customer, customer.Role);
    }

    [Fact]
    public void ChangeRole_RecordsWhoMadeTheChange()
    {
        var customer = Register();
        var admin = Guid.NewGuid();

        Assert.False(customer.ChangeRole(CustomerRole.Admin, admin, Now).IsError);
        Assert.Equal(CustomerRole.Admin, customer.Role);

        var raised = customer.DomainEvents.OfType<CustomerRoleChangedEvent>().Single();

        Assert.Equal(CustomerRole.Customer, raised.PreviousRole);
        Assert.Equal(CustomerRole.Admin, raised.NewRole);
        Assert.Equal(admin, raised.ChangedByCustomerId);
    }

    // ----------------------------------------------------------------- sign-in and lockout

    [Fact]
    public void RecordSignInAttempt_AlwaysProducesAnAuditRow()
    {
        var customer = Register();

        var failure = customer.RecordSignInAttempt(false, "client", Now);
        var success = customer.RecordSignInAttempt(true, "client", Now);

        // The failing path is the one that must not be able to skip the write, so the method
        // returns the attempt rather than an ErrorOr — see Customer.RecordSignInAttempt.
        Assert.Equal(AuthOutcome.BadPassword, failure.Outcome);
        Assert.Equal(AuthOutcome.Succeeded, success.Outcome);
        Assert.Equal(customer.Id, failure.CustomerId);
    }

    [Fact]
    public void FiveFailures_StartALockout()
    {
        var customer = Register();

        for (var attempt = 1; attempt < PasswordCredential.FailuresBeforeLockout; attempt++)
        {
            customer.RecordSignInAttempt(false, null, Now);
            Assert.False(customer.Credential.IsLockedOutAt(Now));
        }

        customer.RecordSignInAttempt(false, null, Now);

        Assert.True(customer.Credential.IsLockedOutAt(Now));
        Assert.Single(customer.DomainEvents.OfType<CustomerLockedOutEvent>());
    }

    [Fact]
    public void ALockedOutAccount_RejectsEvenTheCorrectPassword()
    {
        var customer = Register();

        for (var attempt = 0; attempt < PasswordCredential.FailuresBeforeLockout; attempt++)
        {
            customer.RecordSignInAttempt(false, null, Now);
        }

        // The whole point of the lockout. If it only throttled wrong guesses, the attacker who
        // finally guessed right would be let straight in.
        var attemptWithCorrectPassword = customer.RecordSignInAttempt(true, null, Now);

        Assert.Equal(AuthOutcome.LockedOut, attemptWithCorrectPassword.Outcome);
        Assert.Null(customer.LastSignedInAtUtc);
    }

    [Fact]
    public void TheLockoutExpires_AndBackoffGrowsButIsCapped()
    {
        var customer = Register();

        for (var attempt = 0; attempt < PasswordCredential.FailuresBeforeLockout; attempt++)
        {
            customer.RecordSignInAttempt(false, null, Now);
        }

        var firstWindow = customer.Credential.LockedOutUntilUtc!.Value - Now;
        Assert.Equal(TimeSpan.FromMinutes(1), firstWindow);

        // Past the window, a correct password works again and clears the counter.
        var later = Now.AddMinutes(2);
        var attemptLater = customer.RecordSignInAttempt(true, null, later);

        Assert.Equal(AuthOutcome.Succeeded, attemptLater.Outcome);
        Assert.Equal(0, customer.Credential.FailedAttemptCount);
        Assert.Null(customer.Credential.LockedOutUntilUtc);
    }

    [Fact]
    public void TheBackoffNeverExceedsTheCap()
    {
        var credential = PasswordCredential.Create("verifier", Now).Value;

        for (var attempt = 0; attempt < 100; attempt++)
        {
            credential = credential.RecordFailure(Now);
        }

        // Uncapped doubling would turn a modest guessing attempt into a permanent denial of
        // service against a real customer, which hands the attacker a different win.
        Assert.Equal(TimeSpan.FromMinutes(30), credential.LockedOutUntilUtc!.Value - Now);
    }

    [Fact]
    public void ASuspendedAccount_NeverAuthenticates()
    {
        var customer = Register();
        Assert.False(customer.Suspend(Now).IsError);

        var attempt = customer.RecordSignInAttempt(true, null, Now);

        Assert.Equal(AuthOutcome.NotActive, attempt.Outcome);

        // Reported to the caller as the same generic failure as a wrong password — the outcome
        // is recorded distinctly because the log is for us, not for the caller.
        Assert.Equal("Password.Incorrect", AuthOutcomeErrors.For(attempt.Outcome).Code);
    }

    [Fact]
    public void UpgradingTheVerifier_IsNotAPasswordChange()
    {
        var customer = Register();
        customer.VerifyEmail(Now);

        customer.UpgradeCredential("a-stronger-verifier");

        // Nothing the customer controls changed, so there is no fact about them to record —
        // and no "your password was changed" notification they cannot explain.
        Assert.Empty(customer.DomainEvents.OfType<CustomerPasswordChangedEvent>());
        Assert.Equal(Now, customer.Credential.UpdatedAtUtc);
        Assert.Equal("a-stronger-verifier", customer.Credential.Verifier);
    }

    // ----------------------------------------------------------------- erasure (Onyx D13)

    [Fact]
    public void Anonymise_ErasesEveryPieceOfPersonalData()
    {
        var customer = Register();
        customer.VerifyEmail(Now);
        customer.AddAddress("Home", "1 Main St", null, "Springfield", "11111", "US", false, Now);

        Assert.False(customer.Anonymise(Now).IsError);

        Assert.True(customer.Status.IsAnonymised);
        Assert.Equal("[erased]", customer.Name.Value);
        Assert.DoesNotContain("ada", customer.Email.Value, StringComparison.OrdinalIgnoreCase);
        Assert.True(customer.Credential.IsErased);
        Assert.False(customer.IsEmailVerified);
        Assert.False(customer.CanTransact);

        var address = customer.Addresses.Single();
        Assert.Equal("[erased]", address.Line1);
        Assert.Equal("[erased]", address.City);
        Assert.Null(address.PostalCode);
    }

    [Fact]
    public void Anonymise_KeepsTheEmailUniquePerCustomer()
    {
        // A constant placeholder would collide on the unique index, so the second erasure in the
        // system would fail. The id makes it unique and it stays a valid EmailAddress so nothing
        // downstream has to cope with a malformed one.
        var first = Register("ada@example.com");
        var second = Register("grace@example.com");

        first.Anonymise(Now);
        second.Anonymise(Now);

        Assert.NotEqual(first.Email.Value, second.Email.Value);
        Assert.False(EmailAddress.Create(first.Email.Value).IsError);
    }

    [Fact]
    public void AnErasedCustomer_CannotBeChangedAtAll()
    {
        var customer = Register();
        customer.Anonymise(Now);

        // Erasure is one-way. A system that can restore the data it promised to delete has not
        // deleted it.
        Assert.True(customer.Rename(PersonName.Create("New Name").Value, Now).IsError);
        Assert.True(customer.ChangeRole(CustomerRole.Admin, Guid.NewGuid(), Now).IsError);
        Assert.True(customer.VerifyEmail(Now).IsError);
        Assert.True(customer.ChangePassword("new-verifier", false, Now).IsError);
        Assert.True(customer.Suspend(Now).IsError);
        Assert.True(customer.Reinstate(Now).IsError);
        Assert.True(customer.Anonymise(Now).IsError);
        Assert.True(customer.AddAddress("W", "1 St", null, "City", null, "US", false, Now).IsError);
    }

    // ----------------------------------------------------------------- addresses

    [Fact]
    public void TheFirstAddress_BecomesTheDefaultWhetherOrNotAsked()
    {
        var customer = Register();

        var addressId = customer
            .AddAddress("Home", "1 Main St", null, "Springfield", "11111", "US", false, Now)
            .Value;

        // A customer with addresses and no default is a state nothing downstream can use.
        Assert.Equal(addressId, customer.DefaultAddressId);
    }

    [Fact]
    public void SettingANewDefault_ReplacesTheOldOne()
    {
        var customer = Register();

        var home = customer
            .AddAddress("Home", "1 Main St", null, "Springfield", "11111", "US", false, Now).Value;
        var work = customer
            .AddAddress("Work", "2 Other St", null, "Shelbyville", "22222", "US", false, Now).Value;

        Assert.Equal(home, customer.DefaultAddressId);

        Assert.False(customer.SetDefaultAddress(work, Now).IsError);

        // One column cannot hold two values, so "exactly one default" is a shape rather than an
        // invariant to check. See AddressConfiguration.
        Assert.Equal(work, customer.DefaultAddressId);
        Assert.Equal(work, customer.DefaultAddress!.Id);
    }

    [Fact]
    public void RemovingTheDefault_IsRefusedWhileOtherAddressesRemain()
    {
        var customer = Register();

        var home = customer
            .AddAddress("Home", "1 Main St", null, "Springfield", "11111", "US", false, Now).Value;
        customer.AddAddress("Work", "2 Other St", null, "Shelbyville", "22222", "US", false, Now);

        // Silently promoting an arbitrary address would change where the customer's next order
        // ships without telling them.
        var result = customer.RemoveAddress(home, Now);

        Assert.True(result.IsError);
        Assert.Equal("Address.CannotRemoveLastDefault", result.FirstError.Code);
    }

    [Fact]
    public void RemovingTheLastAddress_ClearsTheDefault()
    {
        var customer = Register();

        var home = customer
            .AddAddress("Home", "1 Main St", null, "Springfield", "11111", "US", false, Now).Value;

        Assert.False(customer.RemoveAddress(home, Now).IsError);

        // A DefaultAddressId pointing at a removed row would be a dangling reference inside the
        // aggregate — and the reason it is safe to hold one with no database foreign key is that
        // RemoveAddress is the only place a row disappears.
        Assert.Null(customer.DefaultAddressId);
        Assert.Null(customer.DefaultAddress);
    }

    [Fact]
    public void AnAddressInAnUnsupportedCountry_IsRejected()
    {
        var customer = Register();

        // Validated against Country, the same definition Catalog prices against and the quote
        // takes its tax rate from — so a checkout cannot later find it has nowhere to ship to.
        var result = customer.AddAddress("Home", "1 Main St", null, "London", "SW1", "GB", false, Now);

        Assert.True(result.IsError);
        Assert.Equal("Country.NotSupported", result.FirstError.Code);
    }

    [Fact]
    public void AnotherCustomersAddressId_IsNotFound_NotAnEdit()
    {
        var mine = Register("ada@example.com");
        var theirs = Register("grace@example.com");

        var theirAddress = theirs
            .AddAddress("Home", "1 Main St", null, "Springfield", "11111", "US", false, Now).Value;

        // The ownership rule one layer in from the authorization policy, so it holds for any
        // future caller that is not an endpoint. Onyx S4 had it at neither layer.
        Assert.True(mine.UpdateAddress(
            theirAddress, "Home", "9 Hack St", null, "Nowhere", null, "US", Now).IsError);
        Assert.True(mine.RemoveAddress(theirAddress, Now).IsError);
        Assert.True(mine.SetDefaultAddress(theirAddress, Now).IsError);
    }

    [Fact]
    public void TheAddressLimitIsEnforced()
    {
        var customer = Register();

        for (var index = 0; index < Customer.MaxAddresses; index++)
        {
            Assert.False(customer
                .AddAddress($"A{index}", "1 Main St", null, "Springfield", null, "US", false, Now)
                .IsError);
        }

        Assert.True(customer
            .AddAddress("One more", "1 Main St", null, "Springfield", null, "US", false, Now)
            .IsError);
    }
}
