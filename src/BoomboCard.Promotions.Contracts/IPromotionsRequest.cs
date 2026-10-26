using BoomboCard.SharedKernel;

namespace BoomboCard.Promotions.Contracts;

/// <summary>
/// Marks a request as a Promotions command: it mutates Promotions state, and so must run inside
/// a transaction on Promotions' context.
///
/// See <see cref="IUnitOfWork{TScope}"/> for why the module has to be carried by the request
/// type rather than inferred from the handler, and <c>ModuleWiringTests</c> for what asserts
/// that every command carries exactly one of these.
/// </summary>
public interface IPromotionsRequest : ITransactionalRequest;
