using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis;

/// <summary>
/// Redis-backed <see cref="IDistributedLockProvider"/>. Acquire is <c>SET key token NX PX</c>;
/// renew and release are owner-checked Lua scripts (compare-and-extend, compare-and-delete).
/// </summary>
/// <remarks>
/// Lease durations are normalized through <see cref="RedisLease"/>, so a positive sub-millisecond lease
/// can never reach Redis as <c>PX 0</c> - which is not a very short lease but a DELETE.
/// </remarks>
[BackendName("redis")]
public sealed class RedisLockProvider : IDistributedLockProvider
{
    private const string RenewScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) else return 0 end";

    private const string ReleaseScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";

    // Acquire and mint the fencing token as ONE atomic step. Splitting them - SET NX, then INCR - would
    // let the process die between the two and hand the next holder a token no higher than ours, or let
    // two acquirers interleave their INCRs and read each other's number. KEYS[2] is the counter; it is
    // never given a TTL and never deleted, because a counter that restarted would repeat a token that
    // some resource has already accepted. INCR on a missing key starts at 1, so 0 can only ever mean
    // "lost the race" and needs no separate nil reply to distinguish it.
    private const string FencedAcquireScript =
        "if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then "
        + "return redis.call('INCR', KEYS[2]) else return 0 end";

    private readonly IConnectionMultiplexer multiplexer;
    private readonly RedisLockOptions options;

    /// <summary>Creates the provider over an existing Redis connection.</summary>
    public RedisLockProvider(IConnectionMultiplexer multiplexer, RedisLockOptions options)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentNullException.ThrowIfNull(options);
        this.multiplexer = multiplexer;
        this.options = options;
    }

    private IDatabase Db => multiplexer.GetDatabase(options.Database);

    private RedisKey Key(string key) => options.KeyPrefix + key;

    // The counter key carries a hash tag around the WHOLE lock key so Redis Cluster routes both keys of
    // the acquire script to the same slot. A lock key containing a brace of its own truncates the tag and
    // the cluster answers CROSSSLOT - a loud failure at acquire time, not a silent wrong answer.
    private RedisKey FenceKey(string key) => $"{{{options.KeyPrefix}{key}}}:fence";

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => (await TryAcquireFencedAsync(key, ownerToken, leaseDuration, cancellationToken).ConfigureAwait(false)).Acquired;

    /// <inheritdoc />
    /// <remarks>
    /// Reports a token only when <see cref="RedisLockOptions.FencingTokens"/> is on; see that property
    /// for why minting one is not free and therefore not the default.
    /// </remarks>
    public async Task<LockAcquisition> TryAcquireFencedAsync(
        string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        // StackExchange.Redis takes no CancellationToken on these calls, so the token is observed here
        // rather than silently ignored: a caller that already gave up does not go on to take the lock.
        cancellationToken.ThrowIfCancellationRequested();

        var expiryMs = RedisLease.ToMilliseconds(leaseDuration);

        if (!options.FencingTokens)
        {
            // keepTtl is spelled out so this binds to the current StringSetAsync overload rather than the
            // legacy one the compiler would otherwise pick.
            var taken = await Db.StringSetAsync(
                Key(key), ownerToken, TimeSpan.FromMilliseconds(expiryMs), keepTtl: false, when: When.NotExists)
                .ConfigureAwait(false);
            return taken ? LockAcquisition.Unfenced : LockAcquisition.NotAcquired;
        }

        var result = await Db.ScriptEvaluateAsync(
            FencedAcquireScript,
            new[] { Key(key), FenceKey(key) },
            new RedisValue[] { ownerToken, expiryMs }).ConfigureAwait(false);

        var token = (long)result;
        return token > 0 ? LockAcquisition.Fenced(token) : LockAcquisition.NotAcquired;
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await Db.ScriptEvaluateAsync(
            RenewScript,
            new RedisKey[] { Key(key) },
            new RedisValue[] { ownerToken, RedisLease.ToMilliseconds(leaseDuration) }).ConfigureAwait(false);
        return (long)result == 1;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        // Deliberately NOT cancellation-checked: dropping a release because the caller's token was
        // cancelled would strand the lock until its lease expires.

        await Db.ScriptEvaluateAsync(
            ReleaseScript,
            new RedisKey[] { Key(key) },
            new RedisValue[] { ownerToken }).ConfigureAwait(false);
    }
}
