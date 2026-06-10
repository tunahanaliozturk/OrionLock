namespace Moongazing.OrionLock.ZooKeeper;

using global::org.apache.zookeeper;

/// <summary>
/// Default <see cref="IZooKeeperClientAdapter"/> over the official <c>ZooKeeperNetEx</c>
/// client. Production wiring; unit tests substitute mocks. The supplied
/// <see cref="org.apache.zookeeper.ZooKeeper"/> instance is owned by the consumer.
/// </summary>
public sealed class DefaultZooKeeperClientAdapter : IZooKeeperClientAdapter
{
    private readonly ZooKeeper client;
    private readonly IZooKeeperAclFactory aclFactory;

    /// <summary>
    /// v0.3.7 source-compatible 1-arg ctor. Defaults the ACL factory to
    /// <see cref="OpenZooKeeperAclFactory"/> so compiled v0.3.7 callers keep the
    /// <c>OPEN_ACL_UNSAFE</c> behaviour.
    /// </summary>
    public DefaultZooKeeperClientAdapter(ZooKeeper client)
        : this(client, new OpenZooKeeperAclFactory())
    {
    }

    /// <summary>
    /// v0.3.8 ctor with explicit <see cref="IZooKeeperAclFactory"/>. Wire
    /// <see cref="DigestZooKeeperAclFactory"/> here for authenticated ACLs on
    /// multi-tenant ensembles.
    /// </summary>
    public DefaultZooKeeperClientAdapter(ZooKeeper client, IZooKeeperAclFactory aclFactory)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(aclFactory);
        this.client = client;
        this.aclFactory = aclFactory;
    }

    /// <inheritdoc />
    public async Task EnsurePathAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Walk from the root, creating each segment as a PERSISTENT znode if missing.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        foreach (var segment in segments)
        {
            current += "/" + segment;
            try
            {
                if (await client.existsAsync(current).ConfigureAwait(false) is null)
                {
                    await client.createAsync(
                        current,
                        Array.Empty<byte>(),
                        aclFactory.CreatePersistentParentAcl(current),
                        CreateMode.PERSISTENT).ConfigureAwait(false);
                }
            }
            catch (KeeperException.NodeExistsException)
            {
                // Concurrent creator won the race; benign.
            }
        }
    }

    /// <inheritdoc />
    public async Task<string> CreateEphemeralSequentialAsync(
        string parentPath, string childPrefix, byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var childPath = $"{parentPath}/{childPrefix}";
        return await client.createAsync(
            childPath,
            data,
            aclFactory.CreateEphemeralChildAcl(childPath),
            CreateMode.EPHEMERAL_SEQUENTIAL).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetChildrenAsync(string parentPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await client.getChildrenAsync(parentPath, watch: false).ConfigureAwait(false);
        // The children list is unordered from ZK; sort lexically so the caller's
        // lowest-sequence check is correct (ZK appends a 10-digit zero-padded suffix so
        // lexical order matches numeric order).
        var sorted = new List<string>(result.Children);
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await client.deleteAsync(path).ConfigureAwait(false);
        }
        catch (KeeperException.NoNodeException)
        {
            // Already gone (session expiry deleted the ephemeral).
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stat = await client.existsAsync(path).ConfigureAwait(false);
        return stat is not null;
    }
}
