namespace Moongazing.OrionLock.Etcd;

/// <summary>
/// Thin abstraction over the subset of etcd v3 KV / Lease operations OrionLock needs.
/// Production wires <see cref="DefaultEtcdClientAdapter"/> over the official
/// <c>dotnet-etcd</c> client; unit tests substitute a mock so the provider can be exercised
/// without a running etcd cluster.
/// </summary>
public interface IEtcdClientAdapter
{
    /// <summary>Create a new lease with the given TTL (seconds). Returns the lease id.</summary>
    Task<long> LeaseGrantAsync(int ttlSeconds, CancellationToken cancellationToken);

    /// <summary>Keep-alive ping for an existing lease. Returns false when etcd reports the lease no longer exists.</summary>
    Task<bool> LeaseKeepAliveAsync(long leaseId, CancellationToken cancellationToken);

    /// <summary>Revoke a lease. Idempotent.</summary>
    Task LeaseRevokeAsync(long leaseId, CancellationToken cancellationToken);

    /// <summary>
    /// Transactional put-if-absent: write (<paramref name="key"/>, <paramref name="value"/>)
    /// under <paramref name="leaseId"/> ONLY IF the key does not currently exist. Returns
    /// true when the write succeeded (lock acquired), false on contention.
    /// </summary>
    Task<bool> KvPutIfAbsentAsync(string key, string value, long leaseId, CancellationToken cancellationToken);

    /// <summary>
    /// Transactional delete: remove the key ONLY IF its current value matches
    /// <paramref name="expectedValue"/>. Returns true on success. Prevents the holder from
    /// inadvertently releasing a lock that another owner already took over after a lease
    /// expiry race.
    /// </summary>
    Task<bool> KvDeleteIfMatchAsync(string key, string expectedValue, CancellationToken cancellationToken);
}
