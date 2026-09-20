namespace Moongazing.OrionLock.Redis;

/// <summary>
/// Where a fencing counter lives in the Redis keyspace, and the rule that keeps it out of the way of
/// lock keys.
/// </summary>
/// <remarks>
/// <para>
/// Lock keys and counter keys share one physical keyspace, and <see cref="RedisLockOptions.KeyPrefix"/>
/// may be empty - so a counter key derived by decorating the lock key is a key some caller can also ask
/// to lock. That is not a cosmetic clash. Two unrelated locks would contend through one counter, and if
/// the counter key holds an owner token rather than an integer the acquire script's <c>INCR</c> fails at
/// runtime AFTER its <c>SET</c> has already taken the lease - Redis does not roll back earlier writes in
/// a script - leaving the lock held by nobody until it expires.
/// </para>
/// <para>
/// Counters therefore live under a reserved prefix of their own, and a lock key that would land inside
/// it is refused. Naming alone cannot separate the two when the caller controls the entire key, so the
/// refusal is what turns the separation into a guarantee.
/// </para>
/// </remarks>
public static class RedisFencingKeys
{
    /// <summary>
    /// The prefix every fencing counter key starts with. No lock key may resolve into this namespace
    /// while <see cref="RedisLockOptions.FencingTokens"/> is on.
    /// </summary>
    public const string ReservedPrefix = "orionlock-fence:";

    /// <summary>
    /// The counter key for a lock whose PHYSICAL key (prefix already applied) is
    /// <paramref name="physicalLockKey"/>.
    /// </summary>
    /// <remarks>
    /// The lock key goes inside braces so Redis Cluster hashes the counter into the lock key's slot and
    /// the two-key acquire script is not rejected with CROSSSLOT. The braces sit AFTER the reserved
    /// prefix, which is what keeps the discriminator in a fixed leading position the caller's own key
    /// can never occupy. A lock key containing a brace of its own truncates the hash tag and the cluster
    /// answers CROSSSLOT - loudly, at acquire time, rather than silently splitting the counter.
    /// </remarks>
    public static string CounterKey(string physicalLockKey) => $"{ReservedPrefix}{{{physicalLockKey}}}";

    /// <summary>
    /// Throws when <paramref name="physicalLockKey"/> falls inside the counter namespace.
    /// </summary>
    /// <remarks>
    /// Only reachable with a <see cref="RedisLockOptions.KeyPrefix"/> that does not already push lock
    /// keys clear of the reserved prefix - with the shipped <c>orionlock:</c> prefix every physical lock
    /// key starts with that instead, so this can never fire.
    /// </remarks>
    public static void ThrowIfReserved(string physicalLockKey, string paramName)
    {
        if (physicalLockKey.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Lock key resolves to '{physicalLockKey}', which is inside the '{ReservedPrefix}' "
                + "namespace OrionLock reserves for fencing counters. Locking it would make one key both "
                + "a lock and another key's counter. Set RedisLockOptions.KeyPrefix to something that is "
                + $"not empty, or stop using keys beginning with '{ReservedPrefix}'.",
                paramName);
        }
    }
}
