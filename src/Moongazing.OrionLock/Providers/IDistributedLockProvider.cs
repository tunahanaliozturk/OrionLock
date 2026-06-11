namespace Moongazing.OrionLock.Providers;

/// <summary>
/// The raw, single-attempt lock primitive a backend implements. The core OrionLock package
/// composes reentrancy, lease renewal, and blocking-acquire retry on top of this.
/// </summary>
public interface IDistributedLockProvider
{
    /// <summary>Tries once, without waiting, to acquire <paramref name="key"/> for <paramref name="ownerToken"/>.</summary>
    Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Extends the lease if and only if <paramref name="ownerToken"/> still owns it.</summary>
    Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Releases the lock if and only if <paramref name="ownerToken"/> still owns it.</summary>
    Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken);

    /// <summary>
    /// v0.3.21: True when the backend honours <c>leaseDuration</c> as a wall-clock TTL
    /// (Redis, in-memory). False when the backend holds the lock for the lifetime of an
    /// open session/transaction regardless of the supplied duration (PostgreSQL
    /// advisory locks, SQL Server sp_getapplock). Lease-expiration diagnostics
    /// (<c>orionlock.lease.expired_before_release</c>) are gated on this so
    /// session-scoped backends do not produce false positives when a caller legitimately
    /// holds the lock longer than the configured <c>LeaseDuration</c>.
    /// </summary>
    bool LeaseDurationIsTtl => true;
}
