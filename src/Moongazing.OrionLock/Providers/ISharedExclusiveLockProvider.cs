namespace Moongazing.OrionLock.Providers;

/// <summary>
/// v0.4.0: the raw, single-attempt reader-writer lock primitive a backend implements. Multiple
/// concurrent <see cref="LockMode.Shared"/> holders may own a key, OR a single
/// <see cref="LockMode.Exclusive"/> holder. The core OrionLock package composes blocking-acquire
/// retry, lease renewal, and diagnostics on top of this, exactly as it does for the exclusive-only
/// <see cref="IDistributedLockProvider"/>.
/// </summary>
/// <remarks>
/// This contract is intentionally separate from <see cref="IDistributedLockProvider"/> so the
/// exclusive-only fast path and its wire format are completely unchanged. A backend that supports
/// reader-writer semantics implements this in addition to (not instead of) the exclusive provider.
/// Backends that cannot yet model shared holders (the distributed providers as of v0.4.0) simply do
/// not register an implementation; only the in-memory testing backend ships one in this release.
/// </remarks>
public interface ISharedExclusiveLockProvider
{
    /// <summary>
    /// Tries once, without waiting, to acquire <paramref name="key"/> for
    /// <paramref name="ownerToken"/> in the requested <paramref name="mode"/>.
    /// </summary>
    /// <param name="key">The resource key.</param>
    /// <param name="ownerToken">Caller-supplied owner identity (typically a Guid N).</param>
    /// <param name="mode">Shared (read) or exclusive (write).</param>
    /// <param name="leaseDuration">TTL applied on success; the hold expires after this if not renewed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the hold was granted; otherwise <see langword="false"/>.</returns>
    Task<bool> TryAcquireAsync(
        string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>
    /// Extends the lease of the hold owned by <paramref name="ownerToken"/> in
    /// <paramref name="mode"/>, if and only if it still owns it.
    /// </summary>
    Task<bool> TryRenewAsync(
        string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the hold owned by <paramref name="ownerToken"/> in <paramref name="mode"/>, if and
    /// only if it still owns it.
    /// </summary>
    Task ReleaseAsync(string key, string ownerToken, LockMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// True when the backend honours <c>leaseDuration</c> as a wall-clock TTL (in-memory, Redis).
    /// Mirrors <see cref="IDistributedLockProvider.LeaseDurationIsTtl"/> so the shared/exclusive
    /// handle suppresses lease-expiry diagnostics on session-scoped backends.
    /// </summary>
    bool LeaseDurationIsTtl => true;
}
