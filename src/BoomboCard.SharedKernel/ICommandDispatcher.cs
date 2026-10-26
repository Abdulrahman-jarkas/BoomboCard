using MediatR;

namespace BoomboCard.SharedKernel;

/// <summary>
/// Sends a command, retrying from scratch if it lost an optimistic concurrency race.
///
/// Endpoints use this for commands instead of <c>ISender</c> directly. The retry cannot live in
/// a pipeline behaviour, because by the time <c>DbUpdateConcurrencyException</c> surfaces the
/// change tracker is in a poisoned state AND the aggregate instance has already had its domain
/// events popped and its state mutated. Reusing it would re-run the domain method against a
/// stale in-memory value and lose the events.
///
/// So each attempt gets a fresh DI scope, a fresh DbContext, and reloads the aggregate.
/// </summary>
public interface ICommandDispatcher
{
    Task<TResponse> SendAsync<TResponse>(
        IRequest<TResponse> command,
        CancellationToken cancellationToken = default);
}
