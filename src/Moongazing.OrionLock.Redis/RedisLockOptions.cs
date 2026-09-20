namespace Moongazing.OrionLock.Redis;

/// <summary>Configuration for the Redis OrionLock backend.</summary>
public sealed class RedisLockOptions
{
    /// <summary>Prefix prepended to every lock key in Redis. Default <c>orionlock:</c>.</summary>
    public string KeyPrefix { get; set; } = "orionlock:";

    /// <summary>The Redis database index. Default -1 (the connection's default database).</summary>
    public int Database { get; set; } = -1;

    /// <summary>
    /// Whether every acquire also mints a fencing token, exposed as
    /// <see cref="IDistributedLockHandle.FencingToken"/>. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When on, the acquire script does <c>SET NX PX</c> and <c>INCR</c> on a per-key counter as one
    /// Lua call, so the token and the lock are taken in a single atomic step and the token strictly
    /// increases per key across every process that acquires it.
    /// </para>
    /// <para>
    /// <b>It costs storage, which is why it is opt-in.</b> The counter lives in a second key
    /// (<c>{lockKey}:fence</c>) that has NO expiry and is NOT deleted on release - it cannot have one.
    /// A counter that expired would restart at 1 and hand a later holder a token an earlier one already
    /// used, which is precisely the failure a fencing token exists to prevent. So enabling this leaves
    /// one small permanent key per lock key you ever take, and turning it on silently on upgrade would
    /// have been a storage change nobody asked for.
    /// </para>
    /// <para>
    /// The counter key is written with a hash tag so it shares a slot with the lock key on Redis
    /// Cluster. A lock key that itself contains <c>{</c> or <c>}</c> can break that co-location and the
    /// cluster will reject the script with CROSSSLOT - loudly, at acquire time.
    /// </para>
    /// </remarks>
    public bool FencingTokens { get; set; }
}
