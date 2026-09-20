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
        this.options.ValidateAndNormalise();
    }

    /// <inheritdoc />
    /// <remarks>
    /// etcd enforces a whole-second TTL with a documented floor, so this provider cannot honour a lease
    /// below <see cref="EtcdLockOptions.MinLeaseTtlSeconds"/>. It used to raise one silently.
    /// </remarks>
    public TimeSpan MinimumLeaseDuration => TimeSpan.FromSeconds(options.MinLeaseTtlSeconds);

    /// <inheritdoc />
    /// <remarks>
    /// etcd TTLs are whole seconds, so a 2.5 s lease really is a 3 s one. Rounding up rather than down
    /// keeps the hold at least as long as asked for; reporting it here means the caller can see it.
    /// </remarks>
    public TimeSpan EffectiveLeaseDuration(TimeSpan requested)
        => TimeSpan.FromSeconds(LeaseTtlSeconds(requested));

    private string FullKey(string lockKey) => options.KeyPrefix + lockKey;

    // The core refuses anything below MinimumLeaseDuration, so this only rounds.
    private static int LeaseTtlSeconds(TimeSpan requestedLease)
        => (int)Math.Ceiling(requestedLease.TotalSeconds);

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => (await TryAcquireFencedAsync(key, ownerToken, leaseDuration, cancellationToken).ConfigureAwait(false))
            .Acquired;

    /// <inheritdoc />
    /// <remarks>
    /// The fencing token is the mvcc revision the acquiring transaction committed at, taken from that
    /// transaction's own response header. etcd's revision is cluster-wide and strictly increasing for
    /// every committed write, and it survives deletion of the key and expiry of the lease - so a later
    /// acquisition of the same key always commits at a higher revision than an earlier one, across
    /// processes, with nothing for OrionLock to maintain and no extra round trip to pay.
    /// <para>
    /// An <see cref="IEtcdClientAdapter"/> that does not also implement
    /// <see cref="IEtcdFencingAdapter"/> reports no token; the acquire is otherwise identical.
    /// </para>
    /// </remarks>
    public async Task<LockAcquisition> TryAcquireFencedAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        var ttl = LeaseTtlSeconds(leaseDuration);
        var leaseId = await etcd.LeaseGrantAsync(ttl, cancellationToken).ConfigureAwait(false);

        LockAcquisition put;
        try
        {
            put = etcd is IEtcdFencingAdapter fencing
                ? await fencing.KvPutIfAbsentFencedAsync(FullKey(key), ownerToken, leaseId, cancellationToken)
                    .ConfigureAwait(false)
                : await etcd.KvPutIfAbsentAsync(FullKey(key), ownerToken, leaseId, cancellationToken)
                    .ConfigureAwait(false)
                    ? LockAcquisition.Unfenced
                    : LockAcquisition.NotAcquired;
        }
        catch
        {
            // Any failure between lease grant and KV put MUST revoke the orphan lease so we
            // do not leak the slot on the etcd cluster. CancellationToken.None is deliberate
            // so cleanup runs even if the outer call was cancelled.
            await etcd.LeaseRevokeAsync(leaseId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (!put.Acquired)
        {
            await etcd.LeaseRevokeAsync(leaseId, CancellationToken.None).ConfigureAwait(false);
            return LockAcquisition.NotAcquired;
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
        return put;
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

        try
        {
            // Delete-if-match guards the case where the original lease expired and another
            // owner took over the key: we MUST NOT delete the new owner's key. If this
            // call throws we still need to revoke the lease below, otherwise the key stays
            // bound to the now-defunct local mapping until TTL expiry.
            await etcd.KvDeleteIfMatchAsync(FullKey(key), ownerToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Revoke runs unconditionally so we do not leak the slot on the etcd side.
            // CancellationToken.None: the lease MUST be released even if the outer call was
            // cancelled or the delete-if-match threw, otherwise the lease lingers until TTL.
            await etcd.LeaseRevokeAsync(leaseId, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
