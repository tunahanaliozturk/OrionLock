namespace Moongazing.OrionLock.Consul;

/// <summary>
/// Configuration for the Consul-backed <see cref="ConsulLockProvider"/>.
/// </summary>
public sealed class ConsulLockOptions
{
    /// <summary>
    /// KV-path prefix under which lock keys are stored. Default <c>"orionlock/"</c>; full
    /// key becomes <c>"orionlock/{lockKey}"</c>. Override to namespace multiple OrionLock
    /// consumers sharing one Consul cluster.
    /// </summary>
    public string KeyPrefix { get; set; } = "orionlock/";

    /// <summary>
    /// Behaviour applied when a Consul session expires (e.g. node loss). <c>"release"</c>
    /// drops the lock back to the pool; <c>"delete"</c> wipes the KV key entirely. Default
    /// <c>"release"</c>, which matches the OrionLock contract: a stale lease becomes
    /// available again so blocking waiters can proceed.
    /// </summary>
    public string SessionBehavior { get; set; } = "release";

    /// <summary>
    /// Consul session TTL refresh window above the OrionLock lease duration. Consul rejects
    /// session TTLs shorter than 10 seconds, so the provider takes <c>max(LeaseDuration,
    /// MinSessionTtl)</c> as the actual session TTL and renews on
    /// <c>IDistributedLockProvider.TryRenewAsync</c>. Default 10 seconds, the Consul-enforced
    /// floor.
    /// </summary>
    public TimeSpan MinSessionTtl { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Consul session <c>LockDelay</c>. When omitted, Consul applies a default 15-second
    /// LockDelay during which the released key remains unavailable to other sessions. That
    /// silently blocks blocking waiters that timed out within OrionLock's default 10-second
    /// <c>WaitTimeout</c>. Default <see cref="TimeSpan.Zero"/> so a release immediately puts
    /// the lock back in the pool; tune up only for workloads that intentionally want a
    /// quiescence window between handoffs.
    /// </summary>
    public TimeSpan LockDelay { get; set; } = TimeSpan.Zero;
}
