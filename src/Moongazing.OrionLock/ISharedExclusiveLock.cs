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

    /// <summary>
    /// v0.5.0: tries to acquire a shared (read) hold, polling until <paramref name="deadline"/> elapses,
    /// then giving up by returning <see langword="null"/> instead of throwing
    /// <see cref="LockAcquisitionTimeoutException"/>. This is the acquire-or-give-up-by-deadline middle
    /// ground between the single-shot <see cref="TryAcquireSharedAsync(string, DistributedLockOptions, CancellationToken)"/>
    /// and the block-or-throw
    /// <see cref="AcquireSharedAsync"/>, for callers that treat "could not acquire in time" as ordinary
    /// control flow rather than an exceptional condition.
    /// </summary>
    /// <remarks>
    /// This default composes the single-shot try-acquire, which mints a fresh owner token per attempt, so
    /// across retries each attempt is a different logical acquirer. The shipped
    /// <see cref="SharedExclusiveLock"/> overrides this to mint one owner token and reuse it across every
    /// deadline retry (stable fencing identity, no orphaned pending-writer reservation). A custom
    /// implementer wanting that property should likewise override rather than rely on this default.
    /// </remarks>
    /// <param name="key">The resource key.</param>
    /// <param name="deadline">
    /// How long to keep polling before giving up. A non-positive value means a single attempt (no wait).
    /// </param>
    /// <param name="options">Lock options; <see cref="DistributedLockOptions.RetryInterval"/> sets the poll cadence.</param>
    /// <param name="cancellationToken">Cancellation token; a cancel still throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The held handle, or <see langword="null"/> if the deadline lapsed first.</returns>
    Task<IDistributedLockHandle?> TryAcquireSharedAsync(
        string key, TimeSpan deadline, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => DeadlineAcquire.TryAcquireUntilDeadlineAsync(
            (k, o, ct) => TryAcquireSharedAsync(k, o, ct), key, deadline, options, cancellationToken);

    /// <summary>
    /// v0.5.0: tries to acquire an exclusive (write) hold, polling until <paramref name="deadline"/>
    /// elapses, then giving up by returning <see langword="null"/> instead of throwing
    /// <see cref="LockAcquisitionTimeoutException"/>. The exclusive counterpart of
    /// <see cref="TryAcquireSharedAsync(string, TimeSpan, DistributedLockOptions, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// As with the shared deadline overload, this default mints a fresh owner token per attempt; the
    /// shipped <see cref="SharedExclusiveLock"/> overrides it to reuse one owner token across all deadline
    /// retries. Override this in a custom implementation if you need that stable fencing identity.
    /// </remarks>
    /// <param name="key">The resource key.</param>
    /// <param name="deadline">
    /// How long to keep polling before giving up. A non-positive value means a single attempt (no wait).
    /// </param>
    /// <param name="options">Lock options; <see cref="DistributedLockOptions.RetryInterval"/> sets the poll cadence.</param>
    /// <param name="cancellationToken">Cancellation token; a cancel still throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The held handle, or <see langword="null"/> if the deadline lapsed first.</returns>
    Task<IDistributedLockHandle?> TryAcquireExclusiveAsync(
        string key, TimeSpan deadline, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => DeadlineAcquire.TryAcquireUntilDeadlineAsync(
            (k, o, ct) => TryAcquireExclusiveAsync(k, o, ct), key, deadline, options, cancellationToken);
}
