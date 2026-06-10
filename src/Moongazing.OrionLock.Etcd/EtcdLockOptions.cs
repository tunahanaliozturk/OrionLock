namespace Moongazing.OrionLock.Etcd;

/// <summary>
/// Configuration for the etcd-backed <see cref="EtcdLockProvider"/>.
/// </summary>
public sealed class EtcdLockOptions
{
    /// <summary>
    /// Key-path prefix under which lock keys are stored. Default <c>"orionlock/"</c>; the
    /// full etcd key becomes <c>"orionlock/{lockKey}"</c>. Override to namespace multiple
    /// OrionLock consumers sharing one etcd cluster.
    /// </summary>
    public string KeyPrefix { get; set; } = "orionlock/";

    /// <summary>
    /// Minimum lease TTL (seconds). etcd enforces an integer-second TTL with a documented
    /// floor; the provider takes <c>max(LeaseDuration.TotalSeconds, MinLeaseTtlSeconds)</c>
    /// when creating the lease. Default 5 seconds.
    /// </summary>
    public int MinLeaseTtlSeconds { get; set; } = 5;
}
