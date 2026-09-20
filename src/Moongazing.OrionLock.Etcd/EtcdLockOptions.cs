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
    /// The shortest lease this backend can honour, in seconds. etcd enforces an integer-second TTL with
    /// a documented floor, so the provider advertises this as its
    /// <c>IDistributedLockProvider.MinimumLeaseDuration</c> and the core refuses a shorter
    /// <c>LeaseDuration</c> with <see cref="ArgumentOutOfRangeException"/> at acquire time. A lease at or
    /// above it is rounded UP to a whole second, which <c>handle.EffectiveLeaseDuration</c> reports.
    /// Default 5 seconds.
    /// </summary>
    /// <remarks>
    /// The provider used to take <c>max(ceil(LeaseDuration), MinLeaseTtlSeconds)</c> silently, so a
    /// caller who asked for 2 seconds got 5 with no warning.
    /// </remarks>
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
