using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Internal;

/// <summary>
/// v0.4.0: the concrete handle for a held reader-writer hold. Mirrors
/// <see cref="DistributedLockHandle"/>: it runs a background watchdog that renews the lease at
/// <c>LeaseDuration / 3</c> intervals (when <see cref="DistributedLockOptions.AutoRenew"/> is set);
/// on renewal failure it flips <see cref="IsHeld"/> and trips <see cref="LostToken"/>. Disposing
/// stops the watchdog and releases the hold in its <see cref="LockMode"/>.
/// </summary>
/// <remarks>
/// The mirroring is now literal: both handles drive the same <see cref="LeaseWatchdog"/>, so the
/// lease, renewal, release, diagnostics and observer semantics are the same code rather than the same
/// intention written twice. All this type still owns is how to renew and release in its mode.
/// </remarks>
internal sealed class SharedExclusiveLockHandle : IDistributedLockHandle
{
    private readonly LockMode mode;
    private readonly LeaseWatchdog lease;

    /// <summary>Creates a handle and, when <see cref="DistributedLockOptions.AutoRenew"/> is set, starts the watchdog.</summary>
    public SharedExclusiveLockHandle(
        ISharedExclusiveLockProvider provider, string key, string ownerToken, LockMode mode,
        DistributedLockOptions options)
        : this(provider, key, ownerToken, mode, options, nowUtc: null, eventObserver: null)
    {
    }

    /// <summary>
    /// Overload that wires the optional <see cref="ILockEventObserver"/> so a reader-writer hold
    /// fires <c>OnLeaseLost</c> / <c>OnReleased</c> exactly as an exclusive one does.
    /// </summary>
    public SharedExclusiveLockHandle(
        ISharedExclusiveLockProvider provider, string key, string ownerToken, LockMode mode,
        DistributedLockOptions options, ILockEventObserver? eventObserver)
        : this(provider, key, ownerToken, mode, options, nowUtc: null, eventObserver)
    {
    }

    /// <summary>
    /// Test-only ctor exposing a clock hook so the fairness watchdog grace period can be driven
    /// deterministically. Production code uses the public overloads which bind the clock to
    /// <see cref="DateTime.UtcNow"/>.
    /// </summary>
    internal SharedExclusiveLockHandle(
        ISharedExclusiveLockProvider provider, string key, string ownerToken, LockMode mode,
        DistributedLockOptions options, Func<DateTime>? nowUtc, ILockEventObserver? eventObserver = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        Key = key;
        this.mode = mode;
        lease = new LeaseWatchdog(
            key,
            (leaseDuration, ct) => provider.TryRenewAsync(key, ownerToken, mode, leaseDuration, ct),
            () => provider.ReleaseAsync(key, ownerToken, mode, CancellationToken.None),
            provider.LeaseDurationIsTtl,
            options,
            nowUtc,
            eventObserver);
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <summary>The mode (shared or exclusive) this hold was acquired in.</summary>
    public LockMode Mode => mode;

    /// <inheritdoc />
    public bool IsHeld => lease.IsHeld;

    /// <inheritdoc />
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Always <see langword="null"/> for a reader-writer hold, and deliberately so.
    /// </para>
    /// <para>
    /// A <see cref="LockMode.Shared"/> hold must NEVER carry one. Readers are concurrent by definition,
    /// so there is no order among them for a token to express: whatever number they were handed would
    /// either collide (two readers, same token - the resource cannot tell them apart) or imply a
    /// sequence the lock never enforced. A fencing token exists to let a resource reject a stale
    /// <em>writer</em>, and a reader is not writing.
    /// </para>
    /// <para>
    /// A <see cref="LockMode.Exclusive"/> hold legitimately could carry one, but none is minted yet:
    /// <see cref="ISharedExclusiveLockProvider"/> has no fenced acquire, and adding the plumbing before
    /// a backend fills it would only produce a property that is null for a second reason. When a
    /// reader-writer backend can mint a token for its writer path, that is where it belongs.
    /// </para>
    /// </remarks>
    public long? FencingToken => null;

    public CancellationToken LostToken => lease.LostToken;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => lease.DisposeAsync();
}
