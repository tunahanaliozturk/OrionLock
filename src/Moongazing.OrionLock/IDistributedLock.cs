namespace Moongazing.OrionLock;

/// <summary>Acquires named distributed locks across processes and machines.</summary>
/// <remarks>
/// <para>
/// <b>The exception contract, in full.</b> It used to document only
/// <see cref="LockAcquisitionTimeoutException"/> while raising up to a dozen things, several of them
/// backend-specific, so catching lock trouble meant referencing every backend's driver package and
/// writing a different <c>catch</c> per registration. Every acquire on this interface raises only:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="ArgumentException"/> - the key is not a legal lock key (see <see cref="LockKey"/>).
/// Thrown synchronously, on the caller's own thread.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="ArgumentOutOfRangeException"/> - a <see cref="DistributedLockOptions"/> value is out of
/// range, or <see cref="DistributedLockOptions.LeaseDuration"/> is shorter than the registered backend
/// can honour. Also thrown synchronously.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="LockAcquisitionTimeoutException"/> - from <see cref="AcquireAsync"/> only, when the lock
/// could not be acquired before <see cref="DistributedLockOptions.WaitTimeout"/>. The
/// <c>TryAcquireAsync</c> overloads return <see langword="null"/> instead.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="OrionLockBackendException"/> - the backend failed for a reason that is not contention.
/// Every driver-level failure arrives as this, whichever backend is registered: a <c>SqlException</c>,
/// <c>PostgresException</c>, <c>RpcException</c>, <c>KeeperException</c>, <c>RedisException</c>,
/// <c>DbException</c> or HTTP failure is wrapped, with the original as
/// <see cref="Exception.InnerException"/>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="OperationCanceledException"/> - the supplied cancellation token was cancelled.
/// Cancellation is control flow and is never wrapped.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="InvalidOperationException"/> - an OrionLock invariant was violated rather than a backend
/// failing; in practice only the ownerToken collision the SQL Server and PostgreSQL providers detect,
/// which is vanishingly unlikely with generated GUIDs.
/// </description>
/// </item>
/// </list>
/// <para>
/// A held lease that is later lost is NOT reported by an exception from these methods:
/// <see cref="IDistributedLockHandle.IsHeld"/> and <see cref="IDistributedLockHandle.LostToken"/>
/// report it, and <see cref="IDistributedLockHandle.ThrowIfLost"/> turns it into a
/// <see cref="LeaseLostException"/> at a point in the critical section you choose.
/// </para>
/// </remarks>
public interface IDistributedLock
{
    /// <summary>
    /// Acquires the lock for <paramref name="key"/>, waiting up to <see cref="DistributedLockOptions.WaitTimeout"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a legal lock key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An <paramref name="options"/> value is out of range, or the lease is shorter than the backend can honour.
    /// </exception>
    /// <exception cref="LockAcquisitionTimeoutException">The lock could not be acquired before the wait timeout.</exception>
    /// <exception cref="OrionLockBackendException">The backend failed for a reason that is not contention.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IDistributedLockHandle> AcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Tries once, without waiting, to acquire the lock. Returns <see langword="null"/> if it is held.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not a legal lock key.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An <paramref name="options"/> value is out of range, or the lease is shorter than the backend can honour.
    /// </exception>
    /// <exception cref="OrionLockBackendException">The backend failed for a reason that is not contention.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
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
