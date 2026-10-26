using BoomboCard.SharedKernel;

namespace BoomboCard.Catalog.Contracts;

/// <summary>
/// Marks a request as a Catalog command: it mutates Catalog state, and so must run inside a
/// transaction on Catalog's context.
///
/// <para>
/// A plain <see cref="ITransactionalRequest"/> is no longer enough to get a transaction, and
/// that is the point. The unit of work is per module, MediatR's open-generic pipeline
/// registration allows exactly two type parameters, and so the module has to be carried by the
/// request type itself — <c>CatalogUnitOfWorkBehavior</c> is constrained to this interface, and
/// the container skips it for anything that does not implement it.
/// </para>
///
/// <para>
/// Public, because <c>DeductStockCommand</c> and <c>RestoreStockCommand</c> are part of
/// Catalog's published surface and other modules send them. It carries no behaviour and no
/// infrastructure, so Contracts stays primitives-only.
/// </para>
///
/// <para>
/// Lives in Contracts rather than in the module for one more reason: it is the honest statement
/// of what sending a Catalog command costs a caller. A Sales handler that sends
/// <c>DeductStockCommand</c> is opening a second transaction, on a second context — not
/// enlisting in its own. Catalog's invariants are Catalog's, and the two commits are sequenced
/// and made recoverable by the checkout session, never welded into one distributed transaction.
/// </para>
/// </summary>
public interface ICatalogRequest : ITransactionalRequest;
