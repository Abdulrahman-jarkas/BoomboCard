namespace BoomboCard.Customers.Contracts;

/// <summary>
/// Who is making this request.
///
/// <para>
/// Implemented by the host over the authenticated principal, and injected into the handlers
/// that need to attribute a change to a person — <c>GrantRoleCommand</c> and the other
/// administrative operations. A command that changes someone's privileges has to record who
/// changed them, and taking the actor as a field on the request would mean a caller could claim
/// to be anybody.
/// </para>
///
/// <para>
/// Onyx had no concept of an actor anywhere: A40 is "no audit trail of any kind", and because
/// S3 let a shopper promote themselves through a profile update, the actor and the beneficiary
/// of a privilege change were usually the same person with nothing recording it.
/// </para>
///
/// <para>
/// <b>This is not a service locator.</b> It is a scoped read of one fact about the current
/// request, declared as a dependency by the handlers that need it. Onyx A25's
/// <c>ServiceLocator.Current</c> was a mutable static resolving arbitrary services from
/// anywhere; the distinction is that this cannot be reached without asking for it, and cannot
/// return anything but the caller's identity.
/// </para>
/// </summary>
public interface ICurrentActor
{
    /// <summary>
    /// The authenticated customer's id, or <c>null</c> for an anonymous or non-customer caller.
    ///
    /// Nullable rather than <c>Guid.Empty</c>, so "nobody is signed in" cannot be confused with
    /// a real id by code that forgets to check — and <c>Customer.ChangeRole</c> rejects
    /// <c>Guid.Empty</c> explicitly for the same reason.
    /// </summary>
    Guid? CustomerId { get; }

    bool IsAdmin { get; }
}
