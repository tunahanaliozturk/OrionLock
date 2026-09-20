using System.Collections.Concurrent;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Testing;

/// <summary>
/// In-process <see cref="IDistributedLockProvider"/> with real lease-expiry semantics, for unit
/// tests that should not depend on a Redis server or a database.
/// </summary>
[BackendName("inmemory")]
public sealed class InMemoryLockProvider : IDistributedLockProvider
{
    private sealed record Lease(string OwnerToken, DateTime ExpiresOnUtc, long FencingToken);

    private readonly ConcurrentDictionary<string, Lease> leases = new();

    // Per-key fencing counter, kept SEPARATELY from the lease so it survives release: the lease entry is
    // removed when the lock is given back, and a counter that lived on it would restart at 1 for the next
    // holder - two acquisitions with the same token, which is the one thing a fencing token may never do.
    // The entry is never removed, so the counter only ever moves forward for the life of the provider.
    private readonly ConcurrentDictionary<string, long> fencingTokens = new();

    // One release signal per contended key. Created by the first waiter, completed and removed by
    // the release that frees the key, so every waiter parked on it wakes at once. Keys that were
    // waited on but never released keep one completed-or-pending TCS until the next release of that
    // key - bounded by the number of distinct keys, which is the right trade for a test double.
    private readonly ConcurrentDictionary<string, TaskCompletionSource> releaseSignals = new();

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => (await TryAcquireFencedAsync(key, ownerToken, leaseDuration, cancellationToken).ConfigureAwait(false)).Acquired;

    /// <inheritdoc />
    /// <remarks>
    /// The token is taken under the same compare-and-swap that grants the lease, so a caller that is
    /// told it holds the lock always holds a token strictly greater than every token handed out for this
    /// key before it. Process-local, like everything else about this provider: that is all the in-memory
    /// backend ever claims, and it is enough to exercise the fencing pattern in a test.
    /// </remarks>
    public Task<LockAcquisition> TryAcquireFencedAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        while (true)
        {
            if (leases.TryGetValue(key, out var existing))
            {
                if (existing.ExpiresOnUtc > now)
                {
                    return Task.FromResult(LockAcquisition.NotAcquired);
                }
                var token = NextToken(key);
                if (leases.TryUpdate(key, new Lease(ownerToken, now + leaseDuration, token), existing))
                {
                    return Task.FromResult(LockAcquisition.Fenced(token));
                }
                continue; // raced, retry
            }
            var firstToken = NextToken(key);
            if (leases.TryAdd(key, new Lease(ownerToken, now + leaseDuration, firstToken)))
            {
                return Task.FromResult(LockAcquisition.Fenced(firstToken));
            }
        }
    }

    // Burning a token on a CAS that then loses the race is fine and intended: a fencing token has to be
    // strictly increasing, not gapless. Handing the loser's number to the winner instead would be the
    // bug, because two racing acquirers would both read the same value.
    private long NextToken(string key)
        => fencingTokens.AddOrUpdate(key, 1L, static (_, current) => current + 1);

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        if (leases.TryGetValue(key, out var existing) && existing.OwnerToken == ownerToken)
        {
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, Lease>>)leases)
                .Remove(new System.Collections.Generic.KeyValuePair<string, Lease>(key, existing));
            // Wake everyone parked on this key. Removing first means a waiter that registers after
            // this point gets a fresh signal rather than an already-completed one it would then
            // wait on forever.
            if (releaseSignals.TryRemove(key, out var signal))
            {
                signal.TrySetResult();
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits on a release signal instead of polling: the waiter parks until the holder releases,
    /// its lease lapses, or the budget runs out, and reports the acquisition with its fencing token. The in-process mirror of what Redis pub/sub, an
    /// etcd watch and a ZooKeeper predecessor watch do for the real backends, so a test can prove a
    /// waiter parks rather than spins without a container anywhere near it.
    /// </summary>
    /// <remarks>
    /// The wait is ALSO bounded by the current holder's remaining lease, because an expiring lease
    /// publishes nothing - nobody releases it. That bound is the only polling left: one wake per
    /// lease expiry rather than one per retry interval.
    /// </remarks>
    public async Task<LockAcquisition> WaitForAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
        LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var infinite = maxWait == Timeout.InfiniteTimeSpan;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Subscribe BEFORE trying. The other order loses a release that lands between the
            // failed attempt and the subscription, and the waiter then sleeps through its chance.
            var signal = releaseSignals.GetOrAdd(
                key, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            // The FENCED attempt, so a lock taken by waiting carries the same token a lock taken on
            // the first attempt would have.
            var acquisition = await TryAcquireFencedAsync(key, ownerToken, leaseDuration, cancellationToken)
                .ConfigureAwait(false);
            if (acquisition.Acquired)
            {
                return acquisition;
            }

            var remaining = maxWait - elapsed.Elapsed;
            if (!infinite && remaining <= TimeSpan.Zero)
            {
                return LockAcquisition.NotAcquired;
            }

            var wait = infinite ? Timeout.InfiniteTimeSpan : remaining;
            if (leases.TryGetValue(key, out var holder))
            {
                var untilExpiry = holder.ExpiresOnUtc - DateTime.UtcNow;
                if (untilExpiry < TimeSpan.Zero)
                {
                    untilExpiry = TimeSpan.Zero;
                }
                // One tick past the expiry so the retry sees the lease as lapsed, not as expiring.
                untilExpiry += TimeSpan.FromMilliseconds(1);
                if (infinite || untilExpiry < wait)
                {
                    wait = untilExpiry;
                }
            }

            try
            {
                await signal.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Either the budget ran out or the holder's lease is due; the loop re-checks both.
            }
        }
    }
}
