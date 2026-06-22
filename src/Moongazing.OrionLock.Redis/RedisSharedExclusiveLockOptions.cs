namespace Moongazing.OrionLock.Redis;

/// <summary>
/// Configuration for the Redis reader-writer (shared/exclusive) OrionLock backend
/// (<see cref="RedisSharedExclusiveLockProvider"/>). Kept separate from
/// <see cref="RedisLockOptions"/> so the exclusive-only fast path and the reader-writer path can be
/// tuned and prefixed independently.
/// </summary>
public sealed class RedisSharedExclusiveLockOptions
{
    /// <summary>
    /// Prefix prepended to every reader-writer lock key in Redis. Default <c>orionlock:rw:</c>.
    /// Distinct from <see cref="RedisLockOptions.KeyPrefix"/> so a reader-writer hold and an
    /// exclusive-only hold on the same logical key name never collide in the keyspace.
    /// </summary>
    public string KeyPrefix { get; set; } = "orionlock:rw:";

    /// <summary>The Redis database index. Default -1 (the connection's default database).</summary>
    public int Database { get; set; } = -1;
}
