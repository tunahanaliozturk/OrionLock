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

    /// <summary>
    /// Validate + normalise the options. Called by the provider constructor. Trims a
    /// trailing slash from a blank-or-empty prefix (we accept either) and rejects a
    /// non-positive <see cref="MinLeaseTtlSeconds"/> so a misconfigured options object
    /// cannot produce a 0-second lease TTL (which etcd would reject at grant time anyway,
    /// but the failure message is clearer here).
    /// </summary>
    internal void ValidateAndNormalise()
    {
        if (MinLeaseTtlSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinLeaseTtlSeconds),
                MinLeaseTtlSeconds,
                "EtcdLockOptions.MinLeaseTtlSeconds must be a positive integer (etcd requires whole-second TTLs).");
        }
        KeyPrefix ??= string.Empty;
    }
}
