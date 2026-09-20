namespace Moongazing.OrionLock.ZooKeeper;

using System.Collections.Concurrent;
using System.Text;
using Moongazing.OrionLock.Providers;

/// <summary>
/// <see cref="IDistributedLockProvider"/> backed by Apache ZooKeeper's ephemeral-sequential
/// znode recipe (the canonical "Distributed Lock" pattern from the ZooKeeper docs).
/// <see cref="TryAcquireAsync"/> creates an EPHEMERAL_SEQUENTIAL child under the lock-key
/// parent znode and declares ownership when it holds the lowest sequence number;
/// <see cref="TryRenewAsync"/> confirms the child still exists (ZooKeeper sessions own the
/// renewal heartbeat, so OrionLock's renew is a liveness check rather than an extension);
/// <see cref="ReleaseAsync"/> deletes the child so the next waiter in line takes over.
/// </summary>
/// <remarks>
/// Session-expiry semantics: ZooKeeper deletes ephemeral znodes when their owning session
/// closes (process crash, network partition past session timeout). OrionLock therefore
/// inherits the broker's liveness guarantees without a TTL of its own; a crashed holder
/// loses the lock the moment the session expires.
/// </remarks>
public sealed class ZooKeeperLockProvider : IDistributedLockProvider
{
    /// <inheritdoc />
    /// <remarks>
    /// A ZooKeeper hold is scoped to the ephemeral znode's SESSION, not to a TTL: the lock lives until
    /// the child znode is deleted or the session expires, and the <c>leaseDuration</c> this provider is
    /// handed is never written anywhere (see <see cref="TryAcquireAsync"/>). Leaving the interface
    /// default of <see langword="true"/> made the core account for a TTL that does not exist, so a caller
    /// legitimately still holding the lock past <c>LeaseDuration</c> raised a false
    /// <c>expired_before_release</c> event, and a connection blip that delayed a renew past the same
    /// window was reported as a lost lease although the session - and therefore the lock - was intact.
    /// Same reasoning as the session-scoped PostgreSQL and SQL Server advisory-lock providers.
    /// </remarks>
    public bool LeaseDurationIsTtl => false;

    private readonly IZooKeeperClientAdapter zk;
    private readonly ZooKeeperLockOptions options;

    // (ownerToken, key) -> created child znode path. Same pair-keying pattern as the
    // Consul and Etcd providers so one owner can legally hold multiple keys.
    private readonly ConcurrentDictionary<(string Owner, string Key), string> ownerKeyToNode = new();

    /// <summary>Construct over a ZooKeeper adapter (production wires <see cref="DefaultZooKeeperClientAdapter"/>).</summary>
    public ZooKeeperLockProvider(IZooKeeperClientAdapter zk, ZooKeeperLockOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(zk);
        this.zk = zk;
        this.options = options ?? new ZooKeeperLockOptions();
        this.options.ValidateAndNormalise();
    }

    // The key becomes exactly ONE znode name: it is encoded rather than concatenated, so a key
    // containing '/' can no longer expand into a chain of PERSISTENT znodes that nothing ever deletes.
    private string ParentPath(string lockKey) => $"{options.RootPath}/{ZooKeeperKeyName.Encode(lockKey)}";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>No fencing token.</b> The two numbers ZooKeeper offers here both reset, so neither is usable:
    /// </para>
    /// <para>
    /// The sequential znode's 10-digit suffix comes from the PARENT's <c>cversion</c>, and this provider
    /// deletes the parent once its last child is gone (see <c>TryPruneParentAsync</c> - the parent is
    /// PERSISTENT, and without pruning every key ever locked leaves a znode in an ensemble that holds its
    /// whole tree in memory). A key that is locked, released and locked again therefore gets sequence
    /// <c>0000000000</c> twice: two acquisitions, one token. The parent's <c>cversion</c> is the same
    /// counter and dies with it.
    /// </para>
    /// <para>
    /// What WOULD work is the created znode's <c>czxid</c> - the ZooKeeper transaction id, which is
    /// ensemble-wide, strictly increasing and never reset, exactly like etcd's revision. ZooKeeper's
    /// create does not return a <c>Stat</c>, so reading it means an extra <c>exists</c> round trip per
    /// acquire and a new method on <see cref="IZooKeeperClientAdapter"/>. That is the upgrade path; until
    /// someone needs it, this provider reports <see langword="null"/> rather than a sequence number that
    /// repeats.
    /// </para>
    /// </remarks>
    public async Task<bool> TryAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);
        // leaseDuration is intentionally unused: ZooKeeper sessions own the heartbeat so the
        // OrionLock lease translates to the session's keep-alive rather than a per-key TTL.

        var parent = ParentPath(key);
        await zk.EnsurePathAsync(parent, cancellationToken).ConfigureAwait(false);

        string created;
        try
        {
            created = await CreateChildAsync(parent, ownerToken, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A release running concurrently can prune an empty parent between our EnsurePath and our
            // create, which would surface as a spurious acquire failure. Re-ensure the parent and try
            // once more; a second failure is a real one (network blip, session close) and propagates.
            // Nothing to clean up either way - the ephemeral child either was not created or will be
            // auto-deleted on session expiry.
            await zk.EnsurePathAsync(parent, cancellationToken).ConfigureAwait(false);
            created = await CreateChildAsync(parent, ownerToken, cancellationToken).ConfigureAwait(false);
        }

        // ZooKeeper's lock recipe: we own the lock when our child znode has the lowest
        // sequence number among all children under the parent. The children list is sorted
        // lexically, which matches numeric ordering for the 10-digit suffix. ALL paths
        // between successful CreateEphemeralSequential and successful mapping-store MUST
        // delete the child on failure so a thrown exception (network blip, cancellation,
        // OOM in the mapping dictionary) does not leak the ephemeral until the session
        // expires - other waiters would otherwise see our orphan child blocking them.
        try
        {
            var children = await zk.GetChildrenAsync(parent, cancellationToken).ConfigureAwait(false);
            var ourName = created[(parent.Length + 1)..];
            var ourIsLowest = children.Count > 0 && children[0] == ourName;

            if (!ourIsLowest)
            {
                await TryDeleteAsync(created).ConfigureAwait(false);
                return false;
            }

            ownerKeyToNode[(ownerToken, key)] = created;
            return true;
        }
        catch
        {
            await TryDeleteAsync(created).ConfigureAwait(false);
            throw;
        }
    }

    private Task<string> CreateChildAsync(string parent, string ownerToken, CancellationToken cancellationToken)
        => zk.CreateEphemeralSequentialAsync(
            parent,
            childPrefix: "lock-",
            data: Encoding.UTF8.GetBytes(ownerToken),
            cancellationToken);

    // Best-effort prune of a lock's parent znode once its last child is gone. The parent is PERSISTENT,
    // so without this every key ever locked leaves a znode behind for the lifetime of the ensemble - and
    // ZooKeeper holds its whole tree in memory. Failure is fine and expected: a concurrent acquirer may
    // create a child between the check and the delete, and ZooKeeper then refuses to delete a non-empty
    // znode. A parent left behind is exactly the old behaviour.
    private async Task TryPruneParentAsync(string parent)
    {
        try
        {
            var children = await zk.GetChildrenAsync(parent, CancellationToken.None).ConfigureAwait(false);
            if (children.Count == 0)
            {
                await zk.DeleteAsync(parent, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // See above: losing this race is the no-op case, not an error worth surfacing.
        }
    }

    private async Task TryDeleteAsync(string node)
    {
        try
        {
            await zk.DeleteAsync(node, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: the znode is ephemeral and will be cleaned up on session close
            // even if delete fails. We never propagate a cleanup failure to the caller.
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        if (!ownerKeyToNode.TryGetValue((ownerToken, key), out var node))
        {
            return false;
        }
        var stillThere = await zk.ExistsAsync(node, cancellationToken).ConfigureAwait(false);
        if (!stillThere)
        {
            ownerKeyToNode.TryRemove((ownerToken, key), out _);
        }
        return stillThere;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

        if (!ownerKeyToNode.TryRemove((ownerToken, key), out var node))
        {
            return;
        }
        try
        {
            await zk.DeleteAsync(node, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // ZooKeeper auto-deletes the ephemeral on session close, so a delete failure
            // here is benign - the next acquirer sees a clean parent znode as soon as the
            // session expires.
            return;
        }

        await TryPruneParentAsync(ParentPath(key)).ConfigureAwait(false);
    }
}
