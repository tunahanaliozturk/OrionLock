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

    private string ParentPath(string lockKey) => $"{options.RootPath}/{lockKey}";

    /// <inheritdoc />
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
            created = await zk.CreateEphemeralSequentialAsync(
                parent,
                childPrefix: "lock-",
                data: Encoding.UTF8.GetBytes(ownerToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // EnsurePath succeeded but the child create failed (network blip, session
            // close). Nothing to clean up - the ephemeral child either was not created or
            // will be auto-deleted on session expiry.
            throw;
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
        }
    }
}
