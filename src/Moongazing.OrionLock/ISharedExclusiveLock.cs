namespace Moongazing.OrionLock;

/// <summary>
/// v0.4.0: acquires named reader-writer (shared/exclusive) distributed locks. For a given key,
/// either any number of <see cref="LockMode.Shared"/> holders coexist, OR exactly one
/// <see cref="LockMode.Exclusive"/> holder owns it. Acquire, TTL/lease, renewal, release, options,
/// and diagnostics semantics mirror the exclusive-only <see cref="IDistributedLock"/>.
/// </summary>
public interface ISharedExclusiveLock
{
    /// <summary>
    /// Acquires a shared (read) hold for <paramref name="key"/>, waiting up to
    /// <see cref="DistributedLockOptions.WaitTimeout"/>. Succeeds while no exclusive holder owns the
    /// key, regardless of how many other shared holders do.
    /// </summary>
    /// <exception cref="LockAcquisitionTimeoutException">The hold could not be acquired before the wait timeout.</exception>
    Task<IDistributedLockHandle> AcquireSharedAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Tries once, without waiting, to acquire a shared hold. Returns <see langword="null"/> if an exclusive holder owns the key.</summary>
    Task<IDistributedLockHandle?> TryAcquireSharedAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires an exclusive (write) hold for <paramref name="key"/>, waiting up to
    /// <see cref="DistributedLockOptions.WaitTimeout"/>. Succeeds only when no shared and no
    /// exclusive holder owns the key.
    /// </summary>
    /// <exception cref="LockAcquisitionTimeoutException">The hold could not be acquired before the wait timeout.</exception>
    Task<IDistributedLockHandle> AcquireExclusiveAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Tries once, without waiting, to acquire an exclusive hold. Returns <see langword="null"/> if any holder owns the key.</summary>
    Task<IDistributedLockHandle?> TryAcquireExclusiveAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);
}
