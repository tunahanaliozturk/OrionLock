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
    /// Delete the znode at <paramref name="path"/>. Implementations MUST treat a
    /// missing-node failure as benign (the znode may have been auto-cleaned on session
    /// expiry between the caller's decision to delete and the actual delete call); the
    /// official ZooKeeper client raises <c>NoNodeException</c> in that case and
    /// implementations swallow it. Any other failure (network, ACL, etc.) propagates as
    /// an exception so the caller can react.
    /// </summary>
    Task DeleteAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Returns true when a znode exists at <paramref name="path"/>. Used by the provider to
    /// confirm the holder znode is still alive when answering <c>TryRenewAsync</c>.
    /// </summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// The watching half of the lock recipe: registers an <c>exists</c> watch on
    /// <paramref name="path"/> - the waiter's immediate predecessor - and returns
    /// <see langword="true"/> as soon as it is gone, which is the moment this waiter moves up the
    /// queue. Returns <see langword="true"/> immediately when the node is already absent, and
    /// <see langword="false"/> when <paramref name="maxWait"/> elapses with it still there.
    /// </summary>
    /// <remarks>
    /// A default interface method so an existing third-party adapter keeps compiling and simply
    /// keeps polling: the default answers <see langword="false"/> at once, which
    /// <see cref="ZooKeeperLockProvider"/> reads as "no watches available" and hands the wait back
    /// to the core's poll loop. An implementation MUST leave no watch registered once it returns.
    /// </remarks>
    Task<bool> WaitForNodeDeletedAsync(string path, TimeSpan maxWait, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
