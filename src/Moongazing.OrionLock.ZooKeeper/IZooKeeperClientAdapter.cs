namespace Moongazing.OrionLock.ZooKeeper;

/// <summary>
/// Thin abstraction over the subset of ZooKeeper operations OrionLock needs to implement
/// the ephemeral-sequential lock recipe. Production wires
/// <see cref="DefaultZooKeeperClientAdapter"/> over the official <c>ZooKeeperNetEx</c>
/// client; unit tests substitute mocks so the provider can be exercised without a real
/// ZooKeeper ensemble.
/// </summary>
public interface IZooKeeperClientAdapter
{
    /// <summary>
    /// Ensure the parent znode at <paramref name="path"/> exists (idempotent recursive
    /// create). Returns true when the parent was created or already existed.
    /// </summary>
    Task EnsurePathAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Create an EPHEMERAL_SEQUENTIAL child znode under <paramref name="parentPath"/> with
    /// the supplied <paramref name="data"/>. Returns the full path of the created child
    /// (ZooKeeper appends a 10-digit zero-padded sequence number to the prefix).
    /// </summary>
    Task<string> CreateEphemeralSequentialAsync(string parentPath, string childPrefix, byte[] data, CancellationToken cancellationToken);

    /// <summary>
    /// Return the sorted list of child znode names under <paramref name="parentPath"/>.
    /// Sorting is lexical, which matches numeric ordering for the 10-digit suffix
    /// ZooKeeper assigns.
    /// </summary>
    Task<IReadOnlyList<string>> GetChildrenAsync(string parentPath, CancellationToken cancellationToken);

    /// <summary>
    /// Delete the znode at <paramref name="path"/>. Throws when the node does not exist;
    /// implementations should swallow the NoNodeException as benign during release.
    /// </summary>
    Task DeleteAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Returns true when a znode exists at <paramref name="path"/>. Used by the provider to
    /// confirm the holder znode is still alive when answering <c>TryRenewAsync</c>.
    /// </summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken);
}
