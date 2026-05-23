namespace Moongazing.OrionLock;

/// <summary>Acquires named distributed locks across processes and machines.</summary>
public interface IDistributedLock
{
    /// <summary>
    /// Acquires the lock for <paramref name="key"/>, waiting up to <see cref="DistributedLockOptions.WaitTimeout"/>.
    /// </summary>
    /// <exception cref="LockAcquisitionTimeoutException">The lock could not be acquired before the wait timeout.</exception>
    Task<IDistributedLockHandle> AcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Tries once, without waiting, to acquire the lock. Returns <see langword="null"/> if it is held.</summary>
    Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);
}
