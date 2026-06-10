namespace Moongazing.OrionLock;

/// <summary>Per-acquisition options for <see cref="IDistributedLock"/>.</summary>
public sealed class DistributedLockOptions
{
    /// <summary>How long the lease is valid before it expires. Default 30 seconds.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a blocking <see cref="IDistributedLock.AcquireAsync"/> waits. Default 10 seconds.</summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Delay between acquisition attempts inside a blocking acquire. Default 250 ms.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>When true, a background watchdog re-extends the lease while the handle is alive. Default true.</summary>
    public bool AutoRenew { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, <see cref="IDistributedLock.AcquireAsync"/> consults the
    /// registered <see cref="Fairness.IFifoWaiterCoordinator"/> so callers acquire the lock in
    /// arrival order under contention rather than racing on the polling-retry loop. Default
    /// <see langword="false"/> preserves v0.3.2 behaviour. The coordinator is consulted only
    /// during blocking <c>AcquireAsync</c>; non-blocking <c>TryAcquireAsync</c> bypasses it.
    /// </summary>
    /// <remarks>
    /// Honours the registered DI <see cref="Fairness.IFifoWaiterCoordinator"/> implementation.
    /// In-process coordination ships with <see cref="Fairness.InProcessFifoWaiterCoordinator"/>;
    /// distributed (cross-process) backends are on the v0.3.x roadmap.
    /// </remarks>
    public bool UseFifoWaiterCoordinator { get; set; }

    /// <summary>
    /// Fairness watchdog grace period. When the renewal loop hits an exception (transient
    /// backend fault), v0.3.9 and earlier continued retrying indefinitely. v0.3.10 lets
    /// the watchdog give up after a grace period since the last successful renewal -
    /// after this elapses without a successful renewal, the lock is treated as confirmed
    /// lost and auto-released so a stuck backend cannot perpetually deny new waiters.
    /// Defaults to <see langword="null"/> = the value of <see cref="LeaseDuration"/>
    /// (matching the lease's natural TTL).
    /// </summary>
    public TimeSpan? RenewalFailureGracePeriod { get; set; }
}
