using System.Collections.Concurrent;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Testing;

/// <summary>
/// In-process <see cref="IDistributedLockProvider"/> with real lease-expiry semantics, for unit
/// tests that should not depend on a Redis server or a database.
/// </summary>
public sealed class InMemoryLockProvider : IDistributedLockProvider
{
    private sealed record Lease(string OwnerToken, DateTime ExpiresOnUtc);

    private readonly ConcurrentDictionary<string, Lease> leases = new();

    /// <inheritdoc />
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
                continue; // raced, retry
            }
            if (leases.TryAdd(key, fresh))
            {
                return Task.FromResult(true);
            }
        }
    }

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
