using ErrorOr;

namespace BoomboCard.Customers.Domain.CustomerAggregate;

public static class CustomerErrors
{
    public static Error NotFound(Guid customerId) =>
        Error.NotFound("Customer.NotFound", $"Customer '{customerId}' was not found.");

    /// <summary>
    /// Returned by registration when the address is taken.
    ///
    /// <para>
    /// This does leak that an address is registered, and unlike the login endpoint there is no
    /// way around it: a registration form has to tell the person in front of it that they
    /// already have an account, or they cannot proceed. The standard alternative — accept the
    /// registration silently and email the existing account instead — trades an enumeration
    /// oracle for a worse experience for every honest user who forgot they had signed up. The
    /// mitigation is the rate limiter on this endpoint, not a vaguer error.
    /// </para>
    /// </summary>
    public static Error EmailAlreadyRegistered =>
        Error.Conflict(
            "Customer.EmailAlreadyRegistered",
            "An account with that email address already exists.");

    public static Error EmailAlreadyVerified =>
        Error.Conflict("Customer.EmailAlreadyVerified", "That email address is already verified.");

    /// <summary>
    /// The invariant that closes Onyx S3 from underneath the authorization policy. See
    /// <see cref="Customer.ChangeRole"/>.
    /// </summary>
    public static Error CannotChangeOwnRole =>
        Error.Forbidden(
            "Customer.CannotChangeOwnRole",
            "A customer cannot change their own role.");

    public static Error ActorRequired =>
        Error.Forbidden(
            "Customer.ActorRequired",
            "A role change must name the customer making it.");

    public static Error PasswordTooShort(int minimumLength) =>
        Error.Validation(
            "Customer.PasswordTooShort",
            $"A password must be at least {minimumLength} characters.");

    public static Error PasswordTooLong(int maximumLength) =>
        Error.Validation(
            "Customer.PasswordTooLong",
            $"A password may be at most {maximumLength} characters.");

    public static Error EmailNotVerified =>
        Error.Forbidden(
            "Customer.EmailNotVerified",
            "Verify your email address before continuing.");
}
