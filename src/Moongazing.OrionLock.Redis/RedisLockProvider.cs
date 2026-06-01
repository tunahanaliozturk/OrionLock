using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis;

/// <summary>
/// Redis-backed <see cref="IDistributedLockProvider"/>. Acquire is <c>SET key token NX PX</c>;
/// renew and release are owner-checked Lua scripts (compare-and-extend, compare-and-delete).
/// </summary>
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
        => await Db.StringSetAsync(Key(key), ownerToken, leaseDuration, when: When.NotExists).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var result = await Db.ScriptEvaluateAsync(
            RenewScript,
            new RedisKey[] { Key(key) },
            new RedisValue[] { ownerToken, (long)leaseDuration.TotalMilliseconds }).ConfigureAwait(false);
        return (long)result == 1;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
        => await Db.ScriptEvaluateAsync(
            ReleaseScript,
            new RedisKey[] { Key(key) },
            new RedisValue[] { ownerToken }).ConfigureAwait(false);
}
