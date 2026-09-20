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
    /// (<c>orionlock-fence:{lockKey}</c>) that has NO expiry and is NOT deleted on release - it cannot
    /// have one.
    /// A counter that expired would restart at 1 and hand a later holder a token an earlier one already
    /// used, which is precisely the failure a fencing token exists to prevent. So enabling this leaves
    /// one small permanent key per lock key you ever take, and turning it on silently on upgrade would
    /// have been a storage change nobody asked for.
    /// </para>
    /// <para>
    /// Counter keys live under the reserved <see cref="RedisFencingKeys.ReservedPrefix"/> namespace, and
    /// while this is on, a lock key that would resolve into it is rejected with an
    /// <see cref="ArgumentException"/> - otherwise one physical key would be both a lock and another
    /// key's counter. With the default <see cref="KeyPrefix"/> that rejection can never fire.
    /// </para>
    /// <para>
    /// The counter key is written with a hash tag so it shares a slot with the lock key on Redis
    /// Cluster. A lock key that itself contains <c>{</c> or <c>}</c> can break that co-location and the
    /// cluster will reject the script with CROSSSLOT - loudly, at acquire time.
    /// </para>
    /// </remarks>
    public bool FencingTokens { get; set; }

    /// <summary>
    /// When <see langword="true"/> (the default) a waiter subscribes to a per-key release channel
    /// instead of re-asking Redis every retry interval, and a release publishes to that channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a DEDICATED channel OrionLock publishes to itself, not Redis keyspace notifications.
    /// Keyspace notifications would need <c>notify-keyspace-events</c> enabled on the server, which
    /// is off by default and is not something a client library can turn on - on a managed Redis it
    /// may not be configurable at all. A channel the provider publishes to works on any Redis, on a
    /// replica, and through a proxy, and costs one PUBLISH per release, sent fire-and-forget so it
    /// adds no latency to the release itself.
    /// </para>
    /// <para>
    /// What it does NOT cover: a lock that frees because its TTL lapsed (the holder crashed, or
    /// released through some other client) publishes nothing. A waiter therefore also bounds its
    /// wait by the holder's remaining TTL, so an expiry is noticed once - not once per retry
    /// interval.
    /// </para>
    /// <para>
    /// Set it to <see langword="false"/> on a deployment where pub/sub is unavailable or
    /// undesirable; waiters then fall back to the poll loop every release before v2.1 used.
    /// </para>
    /// </remarks>
    public bool UseReleaseNotifications { get; set; } = true;

    /// <summary>
    /// Suffix appended to the prefixed lock key to form its release channel. Default
    /// <c>:released</c>. Change it only if the derived name collides with a channel your
    /// application already publishes on.
    /// </summary>
    public string ReleaseChannelSuffix { get; set; } = ":released";
}
