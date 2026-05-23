namespace Moongazing.OrionLock.Redis;

/// <summary>Configuration for the Redis OrionLock backend.</summary>
public sealed class RedisLockOptions
{
    /// <summary>Prefix prepended to every lock key in Redis. Default <c>orionlock:</c>.</summary>
    public string KeyPrefix { get; set; } = "orionlock:";

    /// <summary>The Redis database index. Default -1 (the connection's default database).</summary>
    public int Database { get; set; } = -1;
}
