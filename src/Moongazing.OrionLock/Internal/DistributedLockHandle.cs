using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Internal;

/// <summary>
/// The concrete lock handle. Runs a background watchdog that renews the lease at
/// <c>LeaseDuration / 3</c> intervals; on renewal failure it flips <see cref="IsHeld"/> and
/// trips <see cref="LostToken"/>. Disposing stops the watchdog and releases the lock.
/// </summary>
/// <remarks>
/// The lease lifecycle - renewal, surrender, every instrument, the observer callbacks and the
/// single-fire dispose-race guards - lives in <see cref="LeaseWatchdog"/>, shared verbatim with
/// <see cref="SharedExclusiveLockHandle"/>. All this type still owns is how to renew and release an
/// exclusive hold.
/// </remarks>
internal sealed class DistributedLockHandle : IDistributedLockHandle
{
    private readonly LeaseWatchdog lease;

    /// <summary>Creates a handle and, when <see cref="DistributedLockOptions.AutoRenew"/> is set, starts the watchdog.</summary>
    public DistributedLockHandle(
        IDistributedLockProvider provider, string key, string ownerToken, DistributedLockOptions options)
        : this(provider, key, ownerToken, options, nowUtc: null, eventObserver: null)
    {
    }

    /// <summary>
    /// v0.3.25 overload that wires the optional <see cref="ILockEventObserver"/> so the
    /// handle can fire <c>OnLeaseLost</c> / <c>OnReleased</c> lifecycle callbacks.
    /// </summary>
    public DistributedLockHandle(
        IDistributedLockProvider provider, string key, string ownerToken, DistributedLockOptions options,
        ILockEventObserver? eventObserver, long? fencingToken = null)
        : this(provider, key, ownerToken, options, nowUtc: null, eventObserver, fencingToken)
    {
    }

    /// <summary>
    /// Test-only ctor exposing a clock hook so the fairness watchdog grace period can be
    /// driven deterministically. Production code uses the public overloads which bind the
    /// clock to <see cref="DateTime.UtcNow"/>.
    /// </summary>
    internal DistributedLockHandle(
        IDistributedLockProvider provider, string key, string ownerToken, DistributedLockOptions options,
        Func<DateTime>? nowUtc, ILockEventObserver? eventObserver = null, long? fencingToken = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        Key = key;
        lease = new LeaseWatchdog(
            key,
            (leaseDuration, ct) => provider.TryRenewAsync(key, ownerToken, leaseDuration, ct),
            () => provider.ReleaseAsync(key, ownerToken, CancellationToken.None),
            provider.LeaseDurationIsTtl,
            options,
            nowUtc,
            eventObserver);
        FencingToken = fencingToken;
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public bool IsHeld => lease.IsHeld;

    /// <inheritdoc />
    public CancellationToken LostToken => lease.LostToken;

    /// <inheritdoc />
    /// <remarks>
    /// Captured at construction from the acquisition that created this handle and never changes: the
    /// token identifies THIS acquisition, so a renewal must not advance it (a resource that has seen
    /// token N from us would otherwise start rejecting our own later writes as stale) and a lost lease
    /// must not clear it (the number stays a true statement about the acquisition that minted it).
    /// </remarks>
    public long? FencingToken { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => lease.DisposeAsync();
}
