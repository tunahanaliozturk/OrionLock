namespace Moongazing.OrionLock.Redis;

/// <summary>
/// Configuration for <see cref="RedisFifoWaiterCoordinator"/>.
/// </summary>
public sealed class RedisFifoWaiterOptions
{
    /// <summary>
    /// Redis database index to use. Default 0 (matches the default of the lock provider).
    /// </summary>
    public int Database { get; set; }

    /// <summary>
    /// Prefix applied to the per-key sorted set name. Default <c>"orionlock:fifo"</c>;
    /// override to namespace the queue when sharing a Redis instance with other tenants.
    /// </summary>
    public string KeyPrefix { get; set; } = "orionlock:fifo";

    /// <summary>
    /// Polling interval for the head-position check. Default 50 ms. Tune up for
    /// many-key workloads, tune down for latency-sensitive single-key contention.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Time-to-live for queue entries. A waiter whose process crashed without calling
    /// <c>LeaveAsync</c> remains until this TTL elapses; subsequent <c>EnterAsync</c>
    /// / <c>LeaveAsync</c> calls scan the score range and remove stale entries.
    /// Default 5 minutes. Set to <see cref="TimeSpan.Zero"/> to disable pruning (NOT
    /// recommended for production).
    /// </summary>
    public TimeSpan WaiterTtl { get; set; } = TimeSpan.FromMinutes(5);
}
