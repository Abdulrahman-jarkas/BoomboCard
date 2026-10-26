using BoomboCard.Customers.Contracts;
using BoomboCard.Customers.Domain.AuthAttempts;
using BoomboCard.Customers.Domain.CustomerAggregate.Events;
using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Customers.Domain.CustomerAggregate;

/// <summary>
/// A person who can sign in and buy things: identity, credential, role, lifecycle, and a set of
/// addresses.
///
/// Invariants it holds:
///  - Email is unique, because <see cref="EmailAddress"/> normalises and the database has a
///    unique index on the normalised column.
///  - A role is only ever changed by <see cref="ChangeRole"/>, which demands an actor and
///    refuses self-promotion.
///  - A customer with addresses always has a default, and it always names one of theirs.
///  - Nothing about an anonymised customer can change. Erasure is one-way.
///  - Only a customer who is Active and verified may transact.
///
/// <para>
/// <b>What this aggregate is a rebuild of.</b> Onyx's <c>Customer</c> was eight public
/// get-set properties with no behaviour, no validation and no lifecycle, model-bound directly
/// from an HTTP body (S3), serialised directly to an HTTP response including its password hash
/// (S11), looked up by interpolated SQL on the unauthenticated login path (S1), and hard-deleted
/// by an anonymous endpoint (S2, D13). Six Critical findings on one class, every one of them a
/// consequence of the class having no opinions about itself.
/// </para>
///
/// <para>
/// Mutating methods that imply an audit row return the <see cref="AuthAttempt"/> they imply
/// rather than writing it, exactly as <c>Product</c> returns its <c>StockMovement</c> — so the
/// caller cannot produce the state change without the log entry, and the aggregate stays free
/// of persistence concerns.
/// </para>
/// </summary>
public sealed class Customer : AggregateRoot
{
    public const int MaxAddresses = 10;

    private readonly List<Address> _addresses = [];

    public EmailAddress Email { get; private set; } = null!;

    public PersonName Name { get; private set; } = null!;

    public PasswordCredential Credential { get; private set; } = null!;

    public CustomerRole Role { get; private set; } = CustomerRole.Customer;

    public CustomerStatus Status { get; private set; } = CustomerStatus.Active;

    /// <summary>
    /// Set by <see cref="VerifyEmail"/> and never unset. A verified address is a fact about a
    /// moment: mail sent to it was received and acted on. Changing the address would need a
    /// fresh verification, which is why this aggregate has no "change email" operation yet —
    /// see <c>CONCEPTS.md</c>.
    /// </summary>
    public DateTimeOffset? EmailVerifiedAtUtc { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; private init; }

    public DateTimeOffset? LastSignedInAtUtc { get; private set; }

    public IReadOnlyCollection<Address> Addresses => _addresses.AsReadOnly();

    /// <summary>
    /// Which address is the default, held as one nullable id rather than a flag on each address.
    ///
    /// <para>
    /// "At most one default" is therefore not an invariant this aggregate has to maintain — it
    /// is a shape that cannot express the violation. One column holds one value. The flag-plus-
    /// filtered-unique-index alternative needs provider-specific SQL in a module that
    /// deliberately references no provider, and would have enforced the rule on some databases
    /// and not others. See <c>AddressConfiguration</c>.
    /// </para>
    /// </summary>
    public Guid? DefaultAddressId { get; private set; }

    public bool IsEmailVerified => EmailVerifiedAtUtc is not null;

    /// <summary>
    /// The single definition of "may this customer buy something", published through
    /// <c>CustomerSummaryResponse.CanTransact</c> so Sales does not re-derive it.
    ///
    /// Onyx M18 is the cautionary case: its stock sufficiency rule was written twice, as
    /// <c>&lt;=</c> in one place and <c>&lt;</c> in the other, and the two screens disagreed
    /// about the same product. A predicate worth asking in two modules gets defined in one.
    /// </summary>
    public bool CanTransact => Status.IsActive && IsEmailVerified;

    private Customer() { }

    public static Customer Register(
        EmailAddress email,
        PersonName name,
        PasswordCredential credential,
        DateTimeOffset nowUtc)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = email,
            Name = name,
            Credential = credential,
            Role = CustomerRole.Customer,
            Status = CustomerStatus.Active,
            RegisteredAtUtc = nowUtc
        };

        // Always Customer. There is no parameter for this, because a registration endpoint that
        // can produce an administrator is Onyx S3 by another route — and the seeding path uses
        // ChangeRole with an explicit actor like everything else.
        customer.RaiseDomainEvent(new CustomerRegisteredEvent(
            customer.Id,
            email.Value,
            name.Value,
            nowUtc));

        return customer;
    }

    // ---------------------------------------------------------------- profile

    public ErrorOr<Success> Rename(PersonName name, DateTimeOffset nowUtc)
    {
        if (Status.IsAnonymised)
        {
            return CustomerStatusErrors.Anonymised;
        }

        if (name == Name)
        {
            return Result.Success;
        }

        var previous = Name;
        Name = name;

        RaiseDomainEvent(new CustomerProfileUpdatedEvent(Id, previous.Value, name.Value, nowUtc));

        return Result.Success;
    }

    // ---------------------------------------------------------------- credential

    /// <summary>
    /// Records the outcome of a sign-in attempt and returns the audit row for it.
    ///
    /// <para>
    /// The aggregate is told whether the verifier matched; it does not check. That keeps the
    /// comparison — which must be constant-time and must know the hash format — in the
    /// application layer, and keeps this method's job to the part that is actually domain logic:
    /// the lockout decision, the counter reset, and the audit trail.
    /// </para>
    ///
    /// <para>
    /// <b>The lockout is checked before the result is considered</b>, so a locked-out account
    /// rejects even a correct password. Otherwise the lockout only throttles wrong guesses,
    /// which is not what it is for: the attacker who finally guesses right would be let
    /// straight in.
    /// </para>
    ///
    /// <para>
    /// <b>Returns the attempt, never an error.</b> It is tempting to return
    /// <c>ErrorOr&lt;AuthAttempt&gt;</c> and let a failure be an error — but then the failing
    /// path has no room to carry the audit row, and the row for a failure is the one that
    /// matters. So the outcome is data on the attempt, the handler turns it into a response via
    /// <see cref="AuthOutcomeErrors.For"/>, and there is no path on which an attempt happens and
    /// nothing records it. Onyx recorded neither successes nor failures (S12).
    /// </para>
    /// </summary>
    public AuthAttempt RecordSignInAttempt(
        bool verifierMatched,
        string? clientFingerprint,
        DateTimeOffset nowUtc)
    {
        if (Credential.IsLockedOutAt(nowUtc))
        {
            return Attempt(AuthOutcome.LockedOut);
        }

        // Suspended and anonymised accounts never authenticate. Reported to the caller as the
        // same generic failure the wrong-password path returns — "this account exists but is
        // suspended" is information an attacker can use and the owner already has — but recorded
        // distinctly, because the audit log is for us.
        if (!Status.IsActive)
        {
            return Attempt(AuthOutcome.NotActive);
        }

        if (!verifierMatched)
        {
            var wasLockedOut = Credential.LockedOutUntilUtc is not null;

            Credential = Credential.RecordFailure(nowUtc);

            if (!wasLockedOut && Credential.LockedOutUntilUtc is { } until)
            {
                RaiseDomainEvent(new CustomerLockedOutEvent(
                    Id, Credential.FailedAttemptCount, until, nowUtc));
            }

            return Attempt(AuthOutcome.BadPassword);
        }

        Credential = Credential.RecordSuccess();
        LastSignedInAtUtc = nowUtc;

        return Attempt(AuthOutcome.Succeeded);

        AuthAttempt Attempt(AuthOutcome outcome) =>
            AuthAttempt.Create(Id, Email.Value, outcome, clientFingerprint, nowUtc);
    }

    /// <summary>
    /// Adopts a re-hashed verifier after a successful sign-in against an outdated format. Raises
    /// nothing: the customer changed nothing, so there is no fact about them to record.
    /// </summary>
    public void UpgradeCredential(string verifier) =>
        Credential = Credential.UpgradeVerifier(verifier);

    public ErrorOr<Success> ChangePassword(string verifier, bool viaReset, DateTimeOffset nowUtc)
    {
        if (Status.IsAnonymised)
        {
            return CustomerStatusErrors.Anonymised;
        }

        var replaced = Credential.Replace(verifier, nowUtc);

        if (replaced.IsError)
        {
            return replaced.Errors;
        }

        Credential = replaced.Value;

        RaiseDomainEvent(new CustomerPasswordChangedEvent(Id, Email.Value, viaReset, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> VerifyEmail(DateTimeOffset nowUtc)
    {
        if (Status.IsAnonymised)
        {
            return CustomerStatusErrors.Anonymised;
        }

        if (IsEmailVerified)
        {
            return CustomerErrors.EmailAlreadyVerified;
        }

        EmailVerifiedAtUtc = nowUtc;

        RaiseDomainEvent(new CustomerEmailVerifiedEvent(Id, Email.Value, nowUtc));

        return Result.Success;
    }

    // ---------------------------------------------------------------- role and lifecycle

    /// <summary>
    /// Grants or revokes a role.
    ///
    /// <para>
    /// Three things make this the answer to Onyx S3. It is its own operation, so no profile edit
    /// can reach it. It demands an actor, so the change is attributable. And it refuses the case
    /// where the actor is the subject — the exact move S3 permitted, where a shopper sent
    /// <c>"Role": "Admin"</c> in a profile update and became an administrator. Authorisation on
    /// the endpoint already requires the caller to be an administrator; this is the invariant
    /// underneath it, so the rule holds even when called from a seeder, a job, or a future
    /// endpoint whose author forgets the policy.
    /// </para>
    /// </summary>
    public ErrorOr<Success> ChangeRole(
        CustomerRole role,
        Guid changedByCustomerId,
        DateTimeOffset nowUtc)
    {
        if (Status.IsAnonymised)
        {
            return CustomerStatusErrors.Anonymised;
        }

        if (changedByCustomerId == Id)
        {
            return CustomerErrors.CannotChangeOwnRole;
        }

        if (changedByCustomerId == Guid.Empty)
        {
            return CustomerErrors.ActorRequired;
        }

        if (role == Role)
        {
            return Result.Success;
        }

        var previous = Role;
        Role = role;

        RaiseDomainEvent(new CustomerRoleChangedEvent(
            Id, previous, role, changedByCustomerId, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> Suspend(DateTimeOffset nowUtc)
    {
        var status = Status.Suspend();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        RaiseDomainEvent(new CustomerSuspendedEvent(Id, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> Reinstate(DateTimeOffset nowUtc)
    {
        var status = Status.Reinstate();

        if (status.IsError)
        {
            return status.Errors;
        }

        Status = status.Value;

        // Reinstating clears the lockout: an administrator putting the account back is a
        // stronger statement than a counter left over from whatever got it suspended.
        Credential = Credential.RecordSuccess();

        RaiseDomainEvent(new CustomerReinstatedEvent(Id, nowUtc));

        return Result.Success;
    }

    /// <summary>
    /// Erases the personal data and keeps the row.
    ///
    /// <para>
    /// The row survives because orders and payments reference it, and a financial record that
    /// points at a deleted customer is not a record of anything. Onyx D13 removed the row
    /// outright from an anonymous endpoint (S2) — either a foreign-key failure or an orphaned
    /// order history, depending on which constraints existed, and it was not even erasure: the
    /// name had already been copied into every other table that touched it. Hence
    /// <c>CustomerAnonymisedIntegrationEvent</c>, so the copies are erased too.
    /// </para>
    ///
    /// <para>
    /// The email is replaced with a unique per-customer placeholder rather than a constant,
    /// because the column carries a unique index — two erased customers would collide and the
    /// second erasure would fail. It stays a valid <see cref="EmailAddress"/> so nothing
    /// downstream has to cope with a malformed one.
    /// </para>
    /// </summary>
    public ErrorOr<Success> Anonymise(DateTimeOffset nowUtc)
    {
        var status = Status.Anonymise();

        if (status.IsError)
        {
            return status.Errors;
        }

        var placeholder = EmailAddress.Create($"erased-{Id:n}@erased.invalid");

        if (placeholder.IsError)
        {
            return placeholder.Errors;
        }

        Status = status.Value;
        Email = placeholder.Value;
        Name = PersonName.Anonymised;
        Credential = PasswordCredential.Erased(nowUtc);
        EmailVerifiedAtUtc = null;

        foreach (var address in _addresses)
        {
            address.Erase();
        }

        RaiseDomainEvent(new CustomerAnonymisedEvent(Id, nowUtc));

        return Result.Success;
    }

    // ---------------------------------------------------------------- addresses

    public ErrorOr<Guid> AddAddress(
        string? label,
        string? line1,
        string? line2,
        string? city,
        string? postalCode,
        string? countryCode,
        bool makeDefault,
        DateTimeOffset nowUtc)
    {
        if (Status.IsAnonymised)
        {
            return CustomerStatusErrors.Anonymised;
        }

        if (_addresses.Count >= MaxAddresses)
        {
            return AddressErrors.LimitReached;
        }

        var address = Address.Create(Id, label, line1, line2, city, postalCode, countryCode);

        if (address.IsError)
        {
            return address.Errors;
        }

        _addresses.Add(address.Value);

        // The first address is the default whether or not the caller asked, because a customer
        // with addresses and no default is a state nothing downstream can use.
        var isDefault = makeDefault || _addresses.Count == 1;

        RaiseDomainEvent(new CustomerAddressAddedEvent(
            Id, address.Value.Id, address.Value.CountryCode, isDefault, nowUtc));

        if (isDefault)
        {
            SetDefaultAddressInternal(address.Value.Id, nowUtc);
        }

        return address.Value.Id;
    }

    public ErrorOr<Success> UpdateAddress(
        Guid addressId,
        string? label,
        string? line1,
        string? line2,
        string? city,
        string? postalCode,
        string? countryCode,
        DateTimeOffset nowUtc)
    {
        if (Status.IsAnonymised)
        {
            return CustomerStatusErrors.Anonymised;
        }

        var address = _addresses.SingleOrDefault(candidate => candidate.Id == addressId);

        if (address is null)
        {
            return AddressErrors.NotFound(addressId);
        }

        var updated = address.Update(label, line1, line2, city, postalCode, countryCode);

        if (updated.IsError)
        {
            return updated.Errors;
        }

        RaiseDomainEvent(new CustomerAddressUpdatedEvent(
            Id, addressId, address.CountryCode, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> RemoveAddress(Guid addressId, DateTimeOffset nowUtc)
    {
        if (Status.IsAnonymised)
        {
            return CustomerStatusErrors.Anonymised;
        }

        var address = _addresses.SingleOrDefault(candidate => candidate.Id == addressId);

        if (address is null)
        {
            return AddressErrors.NotFound(addressId);
        }

        var isDefault = DefaultAddressId == addressId;

        // Removing the default while others remain would leave the customer with no default,
        // and silently promoting an arbitrary one would change where their next order ships
        // without telling them. So they choose.
        if (isDefault && _addresses.Count > 1)
        {
            return AddressErrors.CannotRemoveLastDefault;
        }

        _addresses.Remove(address);

        if (isDefault)
        {
            // The last address. Clearing this is not optional bookkeeping — a DefaultAddressId
            // pointing at a removed row is a dangling reference inside the aggregate, and the
            // reason it is safe to hold one without a database foreign key is that this is the
            // only place an address is ever removed.
            DefaultAddressId = null;
        }

        RaiseDomainEvent(new CustomerAddressRemovedEvent(Id, addressId, nowUtc));

        return Result.Success;
    }

    public ErrorOr<Success> SetDefaultAddress(Guid addressId, DateTimeOffset nowUtc)
    {
        if (Status.IsAnonymised)
        {
            return CustomerStatusErrors.Anonymised;
        }

        var address = _addresses.SingleOrDefault(candidate => candidate.Id == addressId);

        if (address is null)
        {
            return AddressErrors.NotFound(addressId);
        }

        if (DefaultAddressId == addressId)
        {
            return Result.Success;
        }

        SetDefaultAddressInternal(addressId, nowUtc);

        return Result.Success;
    }

    public Address? DefaultAddress =>
        DefaultAddressId is { } id
            ? _addresses.SingleOrDefault(address => address.Id == id)
            : null;

    /// <summary>
    /// Private, and the only route to <see cref="DefaultAddressId"/>, so the id can only ever be
    /// set to an address this customer actually owns — the callers have both looked it up in
    /// <c>_addresses</c> first.
    /// </summary>
    private void SetDefaultAddressInternal(Guid addressId, DateTimeOffset nowUtc)
    {
        var previous = DefaultAddressId;

        DefaultAddressId = addressId;

        RaiseDomainEvent(new CustomerDefaultAddressChangedEvent(
            Id, previous, addressId, nowUtc));
    }
}
