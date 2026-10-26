using BoomboCard.SharedKernel;
using ErrorOr;

namespace BoomboCard.Customers.Domain.CustomerAggregate;

public enum CustomerStatusValue
{
    /// <summary>Registered and usable. The email may or may not be verified yet.</summary>
    Active = 0,

    /// <summary>
    /// Locked out or suspended. Reversible: the credentials and the history are intact.
    /// </summary>
    Suspended = 1,

    /// <summary>
    /// Personal data erased, financial history preserved, and <b>terminal</b>. There is no path
    /// back, because there is nothing left to come back to — the name, the email and the
    /// credential are gone.
    /// </summary>
    Anonymised = 2
}

/// <summary>
/// The customer lifecycle, with the legal transitions held in the value object rather than in
/// scattered if-statements.
///
/// <para>
/// Onyx had exactly one lifecycle operation: <c>_context.Customers.Remove(customer)</c>, on an
/// anonymous endpoint (S2, D13). A row with orders and payments hanging off it was hard-deleted,
/// which either threw on a foreign key or orphaned the financial record, and which does not
/// achieve erasure anyway — the customer's name was already copied into every notification and
/// report.
/// </para>
///
/// <para>
/// <c>Anonymised</c> is terminal and that is the point. Erasure must be a one-way door, or it is
/// not erasure; a system that can restore the data it promised to delete has not deleted it.
/// </para>
/// </summary>
public sealed class CustomerStatus : ValueObject
{
    public CustomerStatusValue Value { get; }

    private CustomerStatus(CustomerStatusValue value) => Value = value;

    public static CustomerStatus Active => new(CustomerStatusValue.Active);

    public static CustomerStatus From(CustomerStatusValue value) => new(value);

    public bool IsActive => Value is CustomerStatusValue.Active;

    public bool IsSuspended => Value is CustomerStatusValue.Suspended;

    public bool IsAnonymised => Value is CustomerStatusValue.Anonymised;

    public ErrorOr<CustomerStatus> Suspend() =>
        Value switch
        {
            CustomerStatusValue.Suspended => CustomerStatusErrors.AlreadySuspended,
            CustomerStatusValue.Anonymised => CustomerStatusErrors.Anonymised,
            _ => new CustomerStatus(CustomerStatusValue.Suspended)
        };

    public ErrorOr<CustomerStatus> Reinstate() =>
        Value switch
        {
            CustomerStatusValue.Active => CustomerStatusErrors.AlreadyActive,
            CustomerStatusValue.Anonymised => CustomerStatusErrors.Anonymised,
            _ => new CustomerStatus(CustomerStatusValue.Active)
        };

    public ErrorOr<CustomerStatus> Anonymise() =>
        Value switch
        {
            CustomerStatusValue.Anonymised => CustomerStatusErrors.AlreadyAnonymised,
            _ => new CustomerStatus(CustomerStatusValue.Anonymised)
        };

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value.ToString();
}

public static class CustomerStatusErrors
{
    public static Error AlreadyActive =>
        Error.Conflict("CustomerStatus.AlreadyActive", "The customer is already active.");

    public static Error AlreadySuspended =>
        Error.Conflict("CustomerStatus.AlreadySuspended", "The customer is already suspended.");

    public static Error AlreadyAnonymised =>
        Error.Conflict("CustomerStatus.AlreadyAnonymised", "The customer is already anonymised.");

    public static Error Anonymised =>
        Error.Conflict(
            "CustomerStatus.Anonymised",
            "An anonymised customer cannot be changed. Erasure is deliberately one-way.");
}
