using System.Collections.Concurrent;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// A minimal in-process <see cref="IDistributedLockProvider"/> used by the benchmark suite to
/// exercise the real <c>DistributedLock</c> orchestration without reaching for Redis, SQL Server,
/// Postgres, or any other external service. It mirrors the lease semantics of the production
/// in-memory provider closely enough to measure the abstraction cost of acquire and release: the
/// provider work is a single concurrent-dictionary operation, so what is left to measure is the
/// orchestration around it (owner-token generation, handle allocation, reentrancy bookkeeping).
/// </summary>
public sealed class BenchInMemoryLockProvider : IDistributedLockProvider
{
    private sealed record Lease(string OwnerToken, DateTime ExpiresOnUtc);

    private readonly ConcurrentDictionary<string, Lease> leases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> releaseSignals = new(StringComparer.Ordinal);
    private readonly bool eventDriven;

    /// <param name="eventDriven">
    /// True: override <see cref="WaitForAcquireAsync"/> with a per-key release signal, the way a
    /// backend that can subscribe or block server-side does. False: leave the interface default in
    /// place, which is the poll loop every waiter used before v2.1. The contention benchmark runs
    /// both so the difference is one table, not two runs a human has to line up.
    /// </param>
    public BenchInMemoryLockProvider(bool eventDriven = false) => this.eventDriven = eventDriven;

    public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var fresh = new Lease(ownerToken, now + leaseDuration);

        while (true)
        {
            if (leases.TryGetValue(key, out var existing))
            {
                if (existing.ExpiresOnUtc > now)
                {
                    return Task.FromResult(false);
                }
                if (leases.TryUpdate(key, fresh, existing))
                {
                    return Task.FromResult(true);
                }
                continue;
            }
            if (leases.TryAdd(key, fresh))
            {
                return Task.FromResult(true);
            }
        }
    }

    public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        if (leases.TryGetValue(key, out var existing)
            && existing.OwnerToken == ownerToken
            && existing.ExpiresOnUtc > DateTime.UtcNow)
        {
            var renewed = existing with { ExpiresOnUtc = DateTime.UtcNow + leaseDuration };
            return Task.FromResult(leases.TryUpdate(key, renewed, existing));
        }
        return Task.FromResult(false);
    }

    public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        if (leases.TryGetValue(key, out var existing) && existing.OwnerToken == ownerToken)
        {
            ((ICollection<KeyValuePair<string, Lease>>)leases)
                .Remove(new KeyValuePair<string, Lease>(key, existing));
            if (eventDriven && releaseSignals.TryRemove(key, out var signal))
            {
                signal.TrySetResult();
            }
        }
        return Task.CompletedTask;
    }

    public async Task<LockAcquisition> WaitForAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
        LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
    {
        if (!eventDriven)
        {
            // The pre-v2.1 shape: the interface default, which is the poll loop.
            return await DistributedLockProviderExtensions.PollUntilAcquiredAsync(
                this, key, ownerToken, leaseDuration, maxWait, waitPolicy.ToPollOptions(), cancellationToken)
                .ConfigureAwait(false);
        }

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Subscribe before trying, so a release between the two is not slept through.
            var signal = releaseSignals.GetOrAdd(
                key, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            // This bench provider mints no fencing tokens, so an unfenced grant is the honest
            // answer - not a token invented to satisfy the type.
            if (await TryAcquireAsync(key, ownerToken, leaseDuration, cancellationToken).ConfigureAwait(false))
            {
                return LockAcquisition.Unfenced;
            }

            var remaining = maxWait - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return LockAcquisition.NotAcquired;
            }

            try
            {
                await signal.Task.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return LockAcquisition.NotAcquired;
            }
        }
    }
}
