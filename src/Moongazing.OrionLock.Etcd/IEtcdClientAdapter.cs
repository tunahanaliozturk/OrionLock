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

/// <summary>
/// Optional companion to <see cref="IEtcdClientAdapter"/> for an adapter that can report the mvcc
/// revision a successful put committed at. <see cref="DefaultEtcdClientAdapter"/> implements it;
/// <see cref="EtcdLockProvider"/> uses it when present and falls back to
/// <see cref="IEtcdClientAdapter.KvPutIfAbsentAsync"/> when it is not, so an existing custom adapter
/// keeps working and simply reports no fencing token.
/// </summary>
/// <remarks>
/// etcd's revision is the single counter behind the whole keyspace: every committed write advances it,
/// and it never goes backwards or resets - not on key deletion, not on lease expiry, not on leader
/// change. That makes it monotonic per key for free, which is a stronger guarantee than a per-key
/// counter the lock would have to maintain itself, and it comes back in the transaction's own response
/// header, so reading it costs no extra round trip.
/// </remarks>
public interface IEtcdFencingAdapter
{
    /// <summary>
    /// The same transactional put-if-absent as <see cref="IEtcdClientAdapter.KvPutIfAbsentAsync"/>,
    /// additionally reporting the revision the put committed at as the fencing token. A successful put
    /// whose response carried no revision is reported as
    /// <see cref="Providers.LockAcquisition.Unfenced"/> - acquired, no token - and never as a failure:
    /// the lock IS held at that point, and saying otherwise would strand it.
    /// </summary>
    Task<Providers.LockAcquisition> KvPutIfAbsentFencedAsync(
        string key, string value, long leaseId, CancellationToken cancellationToken);
}
