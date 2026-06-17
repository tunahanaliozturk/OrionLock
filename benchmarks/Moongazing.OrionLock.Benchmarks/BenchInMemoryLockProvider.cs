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
        }
        return Task.CompletedTask;
    }
}
