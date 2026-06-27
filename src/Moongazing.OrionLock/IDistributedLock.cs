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

    /// <summary>
    /// v0.6.0: tries to acquire the lock, polling until <paramref name="deadline"/> elapses, then giving
    /// up by returning <see langword="null"/> instead of throwing
    /// <see cref="LockAcquisitionTimeoutException"/>. This is the acquire-or-give-up-by-deadline middle
    /// ground between the single-shot
    /// <see cref="TryAcquireAsync(string, DistributedLockOptions, CancellationToken)"/> and the
    /// block-or-throw <see cref="AcquireAsync"/>, for callers that treat "could not acquire in time" as
    /// ordinary control flow rather than an exceptional condition. It is the exclusive-lock counterpart of
    /// the reader-writer surface's deadline overloads shipped in 0.5.0.
    /// </summary>
    /// <remarks>
    /// This default composes the single-shot try-acquire, which mints a fresh owner token per attempt, so
    /// across retries each attempt is a different logical acquirer. The shipped
    /// <see cref="DistributedLock"/> overrides this to mint one owner token and reuse it across every
    /// deadline retry (stable fencing identity). A custom implementer wanting that property should likewise
    /// override rather than rely on this default.
    /// </remarks>
    /// <param name="key">The resource key.</param>
    /// <param name="deadline">
    /// How long to keep polling before giving up. A non-positive value means a single attempt (no wait).
    /// </param>
    /// <param name="options">Lock options; <see cref="DistributedLockOptions.RetryInterval"/> sets the poll cadence.</param>
    /// <param name="cancellationToken">Cancellation token; a cancel still throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The held handle, or <see langword="null"/> if the deadline lapsed first.</returns>
    Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, TimeSpan deadline, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => DeadlineAcquire.TryAcquireUntilDeadlineAsync(
            (k, o, ct) => TryAcquireAsync(k, o, ct), key, deadline, options, cancellationToken);
}
