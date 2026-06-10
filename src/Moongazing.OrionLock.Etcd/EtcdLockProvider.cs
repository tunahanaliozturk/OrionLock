namespace Moongazing.OrionLock.Etcd;

using System.Collections.Concurrent;
using Moongazing.OrionLock.Providers;

/// <summary>
/// <see cref="IDistributedLockProvider"/> backed by etcd v3 lease-bound keys.
/// <see cref="TryAcquireAsync"/> creates a lease whose TTL matches the OrionLock lease
/// duration and performs a transactional put-if-absent against the lock key with the
/// owner token as the value; <see cref="TryRenewAsync"/> pings the lease's keep-alive;
/// <see cref="ReleaseAsync"/> deletes the key if the value still matches the owner token
/// AND revokes the lease so the key disappears immediately rather than waiting for the
/// TTL to elapse.
/// </summary>
/// <remarks>
/// Lease-expiry semantics: etcd automatically removes the key when the lease elapses
/// without a keep-alive. A crashed holder therefore loses the lock after the TTL window
/// even without any active intervention from OrionLock - the OrionLock dispatcher loop
/// on other instances polls the key and observes it free on the next iteration.
/// </remarks>
public sealed class EtcdLockProvider : IDistributedLockProvider
{
    private readonly IEtcdClientAdapter etcd;
    private readonly EtcdLockOptions options;

    // (ownerToken, key) -> leaseId. Keyed by the same pair the Consul provider uses, for
    // the same reason: the same ownerToken may legally hold multiple keys, and using the
    // token alone would let a Release for key A revoke the lease that holds key B.
    private readonly ConcurrentDictionary<(string Owner, string Key), long> ownerKeyToLease = new();

    /// <summary>Construct over an etcd adapter (production wires <see cref="DefaultEtcdClientAdapter"/>).</summary>
    public EtcdLockProvider(IEtcdClientAdapter etcd, EtcdLockOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(etcd);
        this.etcd = etcd;
        this.options = options ?? new EtcdLockOptions();
    }

    private string FullKey(string lockKey) => options.KeyPrefix + lockKey;

    private int LeaseTtlSeconds(TimeSpan requestedLease)
    {
        var requested = (int)Math.Ceiling(requestedLease.TotalSeconds);
        return requested > options.MinLeaseTtlSeconds ? requested : options.MinLeaseTtlSeconds;
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        var ttl = LeaseTtlSeconds(leaseDuration);
        var leaseId = await etcd.LeaseGrantAsync(ttl, cancellationToken).ConfigureAwait(false);

        bool acquired;
        try
        {
            acquired = await etcd.KvPutIfAbsentAsync(FullKey(key), ownerToken, leaseId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Any failure between lease grant and KV put MUST revoke the orphan lease so we
            // do not leak the slot on the etcd cluster. CancellationToken.None is deliberate
            // so cleanup runs even if the outer call was cancelled.
            await etcd.LeaseRevokeAsync(leaseId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (!acquired)
        {
            await etcd.LeaseRevokeAsync(leaseId, CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        try
        {
            ownerKeyToLease[(ownerToken, key)] = leaseId;
        }
        catch
        {
            // Mapping store failed; release the key (delete-if-match) and revoke the lease
            // so we do not strand state on etcd that the local process cannot recover.
            await etcd.KvDeleteIfMatchAsync(FullKey(key), ownerToken, CancellationToken.None).ConfigureAwait(false);
            await etcd.LeaseRevokeAsync(leaseId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        if (!ownerKeyToLease.TryGetValue((ownerToken, key), out var leaseId))
        {
            return false;
        }

        var renewed = await etcd.LeaseKeepAliveAsync(leaseId, cancellationToken).ConfigureAwait(false);
        if (!renewed)
        {
            // etcd reports the lease is gone; lease is lost. Drop the local mapping so a
            // subsequent renew does not spam the dead lease id.
            ownerKeyToLease.TryRemove((ownerToken, key), out _);
        }
        return renewed;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        if (!ownerKeyToLease.TryRemove((ownerToken, key), out var leaseId))
        {
            return;
        }

        // Delete-if-match guards the case where the original lease expired and another
        // owner took over the key: we MUST NOT delete the new owner's key. The lease
        // revoke runs unconditionally so we do not leak the slot on the etcd side.
        await etcd.KvDeleteIfMatchAsync(FullKey(key), ownerToken, cancellationToken).ConfigureAwait(false);
        await etcd.LeaseRevokeAsync(leaseId, cancellationToken).ConfigureAwait(false);
    }
}
