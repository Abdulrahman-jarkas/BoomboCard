namespace BoomboCard.SharedKernel;

/// <summary>
/// Marks a request that mutates state. UnitOfWorkBehavior wraps these in a transaction and
/// drains domain events inside it.
///
/// Queries deliberately do not implement this. dome-gym's EventualConsistencyMiddleware
/// called BeginTransactionAsync() unconditionally at the top of every request — including
/// pure reads — and held it open for the whole request lifetime.
/// </summary>
public interface ITransactionalRequest;
