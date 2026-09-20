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
        }
        return Task.CompletedTask;
    }
}
