namespace BoomboCard.Customers.Contracts;

/// <summary>
/// What a principal is allowed to be.
///
/// <para>
/// A closed enum, and <b>never model-bound</b>. Onyx S3 accepted a whole <c>Customer</c> entity
/// on <c>PATCH /api/customers/{id}</c> and copied every supplied property over the stored row
/// with <c>SetValues</c>, so any authenticated shopper could send <c>"Role": "Admin"</c> and
/// promote themselves — against an endpoint whose purpose was editing a name. Changing a role
/// here is its own command, separately authorised and audited.
/// </para>
/// </summary>
public enum CustomerRole
{
    Customer = 0,
    Admin = 1
}
