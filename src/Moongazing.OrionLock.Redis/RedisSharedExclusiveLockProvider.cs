using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis;

/// <summary>
/// Redis-backed <see cref="ISharedExclusiveLockProvider"/> (v0.4.2): a distributed reader-writer
/// (shared/exclusive) lock. For a given key, any number of <see cref="LockMode.Shared"/> holders may
/// coexist, OR exactly one <see cref="LockMode.Exclusive"/> holder owns it. Every reader-writer
/// transition is a single atomic Lua script, so mutual exclusion, crash-safe TTLs, renewal, fencing,
/// and writer fairness never depend on a read-then-write round-trip race.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyspace.</b> Each logical key maps to three Redis keys under
/// <see cref="RedisSharedExclusiveLockOptions.KeyPrefix"/>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>{prefix}{key}:w</c> is a string whose value is the writer's fencing token. Present only
///     while an exclusive holder owns the key. Carries a <c>PX</c> TTL so a dead writer's grip
///     expires.
///   </description></item>
///   <item><description>
///     <c>{prefix}{key}:r</c> is a sorted set of live readers. Member = a reader's fencing token,
///     score = that reader's absolute lease-expiry in Redis-server milliseconds. Readers are tracked
///     <i>individually</i> (never a bare counter) so one reader's expiry never frees another's, and a
///     leaked decrement cannot corrupt the set. The set is pruned by score on every acquire / renew /
///     release, and the whole key is given a <c>PEXPIRE</c> covering its furthest reader so a fully
///     crashed reader fleet leaves nothing behind.
///   </description></item>
///   <item><description>
///     <c>{prefix}{key}:pw</c> is a string holding a waiting writer's fencing token: the
///     pending-writer intent marker that drives writer fairness. Carries its own <c>PX</c> TTL
///     (the writer's requested lease) so a writer that crashes or abandons its wait cannot block
///     readers past that TTL.
///   </description></item>
/// </list>
/// <para>
/// <b>Clock.</b> All expiry math uses the Redis server clock via <c>redis.call('TIME')</c> inside
/// the script, so every process compares lease expiries against one authoritative clock with no
/// client-clock-skew hazard. Under the default script-effects replication (Redis 5+), reading
/// <c>TIME</c> and then writing is replication-safe: Redis replicates the resulting write commands,
/// not the script body.
/// </para>
/// <para>
/// <b>Fencing.</b> The caller-supplied <c>ownerToken</c> is the fencing token. Renew and release are
/// owner-checked in Lua (a reader by sorted-set membership, a writer by string equality), so a stale
/// client can never renew or release another holder's share. Releasing an already-expired share is a
/// safe no-op: the member / string is simply absent, so the guarded delete matches nothing.
/// </para>
/// <para>
/// <b>Writer fairness.</b> See <see cref="RedisSharedExclusiveLockProvider"/> XML on
/// <c>TryAcquireAsync</c> and the type-level remarks below for the exact, lease-bounded
/// writer-preference guarantee. It is the distributed analogue of the in-memory provider's
/// pending-writer reservation, and it is preference, not strict FIFO.
/// </para>
/// <para>
/// <b>Reentrancy / same-instance behaviour.</b> Like the in-memory provider, this models no
/// reentrancy: each acquire is an independent share keyed by its own token. A reader re-acquiring
/// shared with a fresh token simply adds a second member; a reader renewing or re-acquiring with its
/// <i>same</i> token refreshes that one member. A writer re-acquiring exclusive with its own token is
/// treated as a refresh of its hold (so the core's reuse-one-token-per-blocking-acquire contract
/// holds). A write-on-read or read-on-write nesting on the same key behaves like any other competing
/// holder and can deadlock if used that way, exactly as documented for the in-memory backend.
/// </para>
/// </remarks>
[BackendName("redis")]
public sealed class RedisSharedExclusiveLockProvider : ISharedExclusiveLockProvider
{
    // Common preamble shared by every script:
    //   KEYS[1] = writer key (:w), KEYS[2] = readers sorted set (:r), KEYS[3] = pending-writer (:pw)
    // Computes the server-clock 'now' in integer milliseconds. TIME returns { seconds, microseconds }
    // as strings; we fold them into ms. Done first so every expiry comparison below uses one clock.
    private const string NowMs =
        "local t = redis.call('TIME'); " +
        "local now = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000); ";

    // Prune readers whose lease-expiry score is at or before 'now', then re-arm or drop the set's TTL.
    // Defined as a Lua helper string spliced into each script (Redis EVAL has no shared library).
    //   After pruning: if the set still has members, PEXPIRE :r to its furthest member's remaining
    //   life so the key cannot outlive its last reader; if empty, DEL :r so no tombstone lingers.
    private const string PruneReaders =
        "redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now); " +
        "local rcount = redis.call('ZCARD', KEYS[2]); " +
        "if rcount > 0 then " +
        "  local furthest = tonumber(redis.call('ZRANGE', KEYS[2], -1, -1, 'WITHSCORES')[2]); " +
        "  local ttl = furthest - now; " +
        "  if ttl < 1 then ttl = 1 end; " +
        "  redis.call('PEXPIRE', KEYS[2], ttl); " +
        "else " +
        "  redis.call('DEL', KEYS[2]); " +
        "end; ";

    // Acquire SHARED. ARGV[1] = reader fencing token, ARGV[2] = lease ms.
    //   Fails (returns 0) if a writer holds the key, or if a pending-writer marker is live AND this
    //   token is not already a member (a NEW reader is held off so in-flight readers can drain; an
    //   existing reader may refresh). Otherwise upserts the member at now+lease, then re-arms the :r
    //   KEY TTL to the FURTHEST live member's expiry via PruneReaders. It must NOT PEXPIRE :r to this
    //   reader's own lease: a short-lived reader joining after a long-lived one would otherwise shrink
    //   the whole key's TTL to its short lease, so when it expired Redis would delete the entire :r set
    //   and evict the still-live long reader, letting a writer acquire while that reader believes it
    //   still holds - a mutual-exclusion violation. Pruning first also drops any dead member whose
    //   stale score would otherwise over-extend the key.
    private const string AcquireSharedScript =
        NowMs +
        "if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end; " +
        "if redis.call('EXISTS', KEYS[3]) == 1 and redis.call('ZSCORE', KEYS[2], ARGV[1]) == false then return 0 end; " +
        "local expiry = now + tonumber(ARGV[2]); " +
        "redis.call('ZADD', KEYS[2], expiry, ARGV[1]); " +
        PruneReaders +
        "return 1;";

    // Acquire EXCLUSIVE. ARGV[1] = writer fencing token, ARGV[2] = lease ms.
    //   Prune readers first. If a different writer holds :w, fail. If any reader remains, this writer
    //   cannot take the hold yet: plant / refresh the pending-writer marker (first writer wins; a
    //   second concurrent writer just keeps retrying without owning the marker) with its own lease
    //   TTL, then fail. Clear path: SET :w to this token with PX lease, and DEL :pw so NO pending
    //   marker (not even another writer's) survives to deny new readers after this writer releases.
    private const string AcquireExclusiveScript =
        NowMs +
        "local w = redis.call('GET', KEYS[1]); " +
        "if w and w ~= ARGV[1] then return 0 end; " +
        PruneReaders +
        "if redis.call('ZCARD', KEYS[2]) > 0 then " +
        "  local pw = redis.call('GET', KEYS[3]); " +
        "  if not pw or pw == ARGV[1] then " +
        "    redis.call('SET', KEYS[3], ARGV[1], 'PX', tonumber(ARGV[2])); " +
        "  end; " +
        "  return 0; " +
        "end; " +
        "redis.call('SET', KEYS[1], ARGV[1], 'PX', tonumber(ARGV[2])); " +
        "redis.call('DEL', KEYS[3]); " +
        "return 1;";

    // Renew SHARED. ARGV[1] = reader token, ARGV[2] = lease ms. Owner-checked by membership: only a
    // reader still in the set can extend, and only its own score moves. Prune first so a renew that
    // races past expiry correctly reports loss (0) instead of resurrecting a dead member; after
    // extending this reader's score, re-arm the :r KEY TTL via PruneReaders so the key always covers
    // its FURTHEST live member - extending one reader must never shrink the key below another
    // still-live reader's expiry.
    private const string RenewSharedScript =
        NowMs +
        PruneReaders +
        "if redis.call('ZSCORE', KEYS[2], ARGV[1]) == false then return 0 end; " +
        "local expiry = now + tonumber(ARGV[2]); " +
        "redis.call('ZADD', KEYS[2], expiry, ARGV[1]); " +
        PruneReaders +
        "return 1;";

    // Renew EXCLUSIVE. ARGV[1] = writer token, ARGV[2] = lease ms. Compare-and-extend on :w.
    private const string RenewExclusiveScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) else return 0 end";

    // Release SHARED. ARGV[1] = reader token. ZREM the member (no-op if already expired / absent),
    // then re-arm or drop :r TTL. Never touches :w or :pw.
    private const string ReleaseSharedScript =
        NowMs +
        "redis.call('ZREM', KEYS[2], ARGV[1]); " +
        PruneReaders +
        "return 1;";

    // Release EXCLUSIVE. ARGV[1] = writer token. Compare-and-delete on :w (no-op if not owner or
    // already expired). Does NOT touch :pw: the pending-writer marker was already cleared when this
    // writer was granted the hold, mirroring the in-memory provider, so there is nothing to clear and
    // clearing here could erase a DIFFERENT writer's freshly planted intent.
    private const string ReleaseExclusiveScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";

    private readonly IConnectionMultiplexer multiplexer;
    private readonly RedisSharedExclusiveLockOptions options;

    /// <summary>Creates the provider over an existing Redis connection.</summary>
    public RedisSharedExclusiveLockProvider(
        IConnectionMultiplexer multiplexer, RedisSharedExclusiveLockOptions options)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentNullException.ThrowIfNull(options);
        this.multiplexer = multiplexer;
        this.options = options;
    }

    private IDatabase Db => multiplexer.GetDatabase(options.Database);

    /// <summary>
    /// Converts a lease <see cref="TimeSpan"/> to the integer-millisecond <c>PX</c> / score value the
    /// Lua scripts use, rejecting non-positive leases and never truncating a positive lease to zero.
    /// </summary>
    /// <remarks>
    /// A raw <c>(long)leaseDuration.TotalMilliseconds</c> would floor a positive sub-millisecond lease
    /// to <c>0</c> (Redis <c>PX 0</c> / a now-relative score that has already passed produces an
    /// immediately-expired, effectively useless lock) and would silently accept zero or negative
    /// durations. Rounding up with <see cref="Math.Ceiling(double)"/> guarantees every positive lease
    /// maps to at least <c>1</c> ms, and the guard makes a non-positive lease a caller error rather
    /// than a silently broken lock. Every lease-to-ms conversion - shared and exclusive acquire and
    /// renew, and (transitively, via <c>ARGV[2]</c>) the pending-writer marker TTL - routes through
    /// here so the normalization can never be bypassed.
    /// </remarks>
    /// <param name="leaseDuration">The requested lease duration.</param>
    /// <returns>The lease length in whole milliseconds, always &gt;= 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="leaseDuration"/> is less than or equal to <see cref="TimeSpan.Zero"/>.
    /// </exception>
    private static long ToLeaseMilliseconds(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration), leaseDuration, "Lease duration must be positive.");
        }

        return (long)Math.Ceiling(leaseDuration.TotalMilliseconds);
    }

    // The three physical keys for one logical key. Hash-tagged on the logical key so a clustered
    // deployment routes all three to the same slot, which a multi-key Lua script requires.
    private RedisKey[] KeysFor(string key)
    {
        var tagged = $"{options.KeyPrefix}{{{key}}}";
        return
        [
            tagged + ":w",
            tagged + ":r",
            tagged + ":pw",
        ];
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(
        string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        var script = mode == LockMode.Shared ? AcquireSharedScript : AcquireExclusiveScript;
        var result = await Db.ScriptEvaluateAsync(
            script,
            KeysFor(key),
            [ownerToken, ToLeaseMilliseconds(leaseDuration)]).ConfigureAwait(false);
        return (long)result == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(
        string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        var script = mode == LockMode.Shared ? RenewSharedScript : RenewExclusiveScript;
        var result = await Db.ScriptEvaluateAsync(
            script,
            KeysFor(key),
            [ownerToken, ToLeaseMilliseconds(leaseDuration)]).ConfigureAwait(false);
        return (long)result == 1;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        string key, string ownerToken, LockMode mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);

        var script = mode == LockMode.Shared ? ReleaseSharedScript : ReleaseExclusiveScript;
        await Db.ScriptEvaluateAsync(
            script,
            KeysFor(key),
            [ownerToken]).ConfigureAwait(false);
    }
}
