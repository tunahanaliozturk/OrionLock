using System.Collections.Concurrent;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Testing;

/// <summary>
/// v0.4.0: in-process <see cref="ISharedExclusiveLockProvider"/> with real lease-expiry semantics,
/// for unit tests that should not depend on a Redis server or a database. Multiple concurrent
/// <see cref="LockMode.Shared"/> holders may own a key, OR a single
/// <see cref="LockMode.Exclusive"/> holder.
/// </summary>
/// <remarks>
/// <para>
/// State per key is guarded by a per-key monitor; this is a test backend so it favours obvious
/// correctness over lock-free cleverness. Expired holds (shared or exclusive) are pruned lazily on
/// every access against <see cref="DateTime.UtcNow"/>, mirroring the exclusive
/// <see cref="InMemoryLockProvider"/>.
/// </para>
/// <para>
/// Writer fairness: when an exclusive acquire fails because shared holders are present, it records a
/// pending-writer reservation on the key. While a reservation is live, NEW shared acquires are
/// refused so the existing shared holders can drain and the waiting writer is not starved by a
/// steady stream of new readers. The reservation is itself leased (it expires on the same
/// <c>leaseDuration</c> the writer requested) so a writer that gives up or crashes mid-wait cannot
/// permanently block readers. Renewing/re-attempting the exclusive acquire refreshes the
/// reservation. This is a best-effort, in-process fairness aid documented as such; cross-process
/// fair ordering is a v0.4.x follow-up.
/// </para>
/// </remarks>
[BackendName("inmemory")]
public sealed class InMemorySharedExclusiveLockProvider : ISharedExclusiveLockProvider
{
    private sealed class KeyState
    {
        // owner token -> shared hold expiry (UTC).
        public Dictionary<string, DateTime> SharedHolders { get; } = [];

        public string? ExclusiveOwner { get; set; }
        public DateTime ExclusiveExpiresUtc { get; set; }

        // Pending-writer reservation: owner token of a writer currently waiting for shared holders
        // to drain, and the UTC instant the reservation lapses if not refreshed.
        public string? PendingWriterOwner { get; set; }
        public DateTime PendingWriterExpiresUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, KeyState> keys = new();

    private static void PruneExpired(KeyState state, DateTime now)
    {
        if (state.SharedHolders.Count > 0)
        {
            var expired = state.SharedHolders
                .Where(kv => kv.Value <= now)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var owner in expired)
            {
                state.SharedHolders.Remove(owner);
            }
        }

        if (state.ExclusiveOwner is not null && state.ExclusiveExpiresUtc <= now)
        {
            state.ExclusiveOwner = null;
        }

        if (state.PendingWriterOwner is not null && state.PendingWriterExpiresUtc <= now)
        {
            state.PendingWriterOwner = null;
        }
    }

    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(
        string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        var state = keys.GetOrAdd(key, static _ => new KeyState());
        var now = DateTime.UtcNow;

        lock (state)
        {
            PruneExpired(state, now);

            return Task.FromResult(mode == LockMode.Shared
                ? TryAcquireShared(state, ownerToken, now, leaseDuration)
                : TryAcquireExclusive(state, ownerToken, now, leaseDuration));
        }
    }

    private static bool TryAcquireShared(KeyState state, string ownerToken, DateTime now, TimeSpan leaseDuration)
    {
        // A live exclusive holder blocks all readers.
        if (state.ExclusiveOwner is not null)
        {
            return false;
        }

        // Writer fairness: refuse NEW shared holders while a writer reservation is live, so existing
        // readers can drain. A reader that ALREADY holds the key is allowed to refresh its own lease
        // (it is part of the set the writer is waiting to drain, not a new arrival).
        if (state.PendingWriterOwner is not null && !state.SharedHolders.ContainsKey(ownerToken))
        {
            return false;
        }

        state.SharedHolders[ownerToken] = now + leaseDuration;
        return true;
    }

    private static bool TryAcquireExclusive(KeyState state, string ownerToken, DateTime now, TimeSpan leaseDuration)
    {
        // Another live exclusive holder blocks this writer (unless it is us re-acquiring, which we
        // treat as a refresh of our own hold).
        if (state.ExclusiveOwner is not null && state.ExclusiveOwner != ownerToken)
        {
            return false;
        }

        if (state.SharedHolders.Count > 0)
        {
            // Shared holders still present: cannot take the write hold yet. Record / refresh THIS
            // writer's reservation so new readers are held off while these drain. First writer to
            // reserve wins; a second concurrent writer simply keeps retrying without a reservation.
            if (state.PendingWriterOwner is null || state.PendingWriterOwner == ownerToken)
            {
                state.PendingWriterOwner = ownerToken;
                state.PendingWriterExpiresUtc = now + leaseDuration;
            }
            return false;
        }

        // Clear path: take (or refresh) the exclusive hold and clear our reservation if we held one.
        state.ExclusiveOwner = ownerToken;
        state.ExclusiveExpiresUtc = now + leaseDuration;
        if (state.PendingWriterOwner == ownerToken)
        {
            state.PendingWriterOwner = null;
        }
        return true;
    }

    /// <inheritdoc />
    public Task<bool> TryRenewAsync(
        string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        if (!keys.TryGetValue(key, out var state))
        {
            return Task.FromResult(false);
        }

        var now = DateTime.UtcNow;
        lock (state)
        {
            PruneExpired(state, now);

            if (mode == LockMode.Shared)
            {
                if (state.SharedHolders.ContainsKey(ownerToken))
                {
                    state.SharedHolders[ownerToken] = now + leaseDuration;
                    return Task.FromResult(true);
                }
                return Task.FromResult(false);
            }

            if (state.ExclusiveOwner == ownerToken)
            {
                state.ExclusiveExpiresUtc = now + leaseDuration;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string key, string ownerToken, LockMode mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        if (!keys.TryGetValue(key, out var state))
        {
            return Task.CompletedTask;
        }

        var now = DateTime.UtcNow;
        lock (state)
        {
            if (mode == LockMode.Shared)
            {
                state.SharedHolders.Remove(ownerToken);
            }
            else if (state.ExclusiveOwner == ownerToken)
            {
                state.ExclusiveOwner = null;
            }

            PruneExpired(state, now);
        }
        return Task.CompletedTask;
    }
}
