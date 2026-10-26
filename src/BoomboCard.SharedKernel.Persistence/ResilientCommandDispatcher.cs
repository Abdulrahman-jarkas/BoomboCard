using BoomboCard.SharedKernel;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BoomboCard.SharedKernel.Persistence;

public sealed class ConcurrencyRetryOptions
{
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Base backoff. Attempt n waits roughly <c>Base * 2^(n-1)</c> plus jitter.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(20);
}

/// <summary>
/// Singleton. Creates its own scope per attempt, so it cannot hold a scoped DbContext.
/// </summary>
public sealed class ResilientCommandDispatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ResilientCommandDispatcher> logger,
    ConcurrencyRetryOptions? options = null) : ICommandDispatcher
{
    private readonly ConcurrencyRetryOptions _options = options ?? new ConcurrencyRetryOptions();

    public async Task<TResponse> SendAsync<TResponse>(
        IRequest<TResponse> command,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            // A brand new scope, so a brand new DbContext with a clean change tracker.
            await using var scope = scopeFactory.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            try
            {
                return await sender.Send(command, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < _options.MaxAttempts)
            {
                // Somebody committed between our read and our write. The version predicate
                // matched zero rows. Reload and re-run the domain rule against the new value —
                // which may now legitimately fail with InsufficientStock, and that is correct.
                logger.LogDebug(
                    "Concurrency conflict on {Command}, attempt {Attempt} of {MaxAttempts}. Retrying.",
                    typeof(TResponse).Name,
                    attempt,
                    _options.MaxAttempts);

                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Exponential backoff with jitter. The jitter matters: without it, two writers that
    /// collided would wait the same interval and collide again in lockstep.
    /// </summary>
    private Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var backoff = _options.BaseDelay * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * backoff.TotalMilliseconds * 0.5;
        var delay = backoff + TimeSpan.FromMilliseconds(jitter);

        return Task.Delay(delay, timeProvider, cancellationToken);
    }
}
