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

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        // StackExchange.Redis takes no CancellationToken on these calls, so the token is observed here
        // rather than silently ignored: a caller that already gave up does not go on to take the lock.
        cancellationToken.ThrowIfCancellationRequested();

        var expiry = TimeSpan.FromMilliseconds(RedisLease.ToMilliseconds(leaseDuration));
        // keepTtl is spelled out so this binds to the current StringSetAsync overload rather than the
        // legacy one the compiler would otherwise pick.
        return await Db.StringSetAsync(Key(key), ownerToken, expiry, keepTtl: false, when: When.NotExists)
            .ConfigureAwait(false);
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
