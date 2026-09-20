using Moongazing.OrionLock.Redis;
using Moq;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

/// <summary>
/// Counter keys and lock keys live in one physical Redis keyspace, and <see cref="RedisLockOptions.KeyPrefix"/>
/// may be empty - so a generated counter key must not be a key any caller could also ask to lock.
/// Otherwise two unrelated locks contend through one counter, and worse: the counter key then holds an
/// owner token rather than an integer, the script's INCR fails at runtime, and Redis does not roll back
/// the SET it already executed - the lease is taken and never handed to anyone.
/// No server needed; the Redis surface is substituted, so these run without Docker.
/// </summary>
public sealed class RedisFencingKeyNamespaceTests
{
    private static IConnectionMultiplexer Multiplexer(IDatabase db)
    {
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db);
        return mux.Object;
    }

    /// <summary>Runs one fenced acquire and returns the keys the script was handed.</summary>
    private static async Task<RedisKey[]> CaptureAcquireKeysAsync(string key, string keyPrefix)
    {
        var db = new Mock<IDatabase>();
        RedisKey[] captured = [];
        db.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Callback((string _, RedisKey[] keys, RedisValue[] _, CommandFlags _) => captured = keys)
            .ReturnsAsync(RedisResult.Create((RedisValue)1L));

        var sut = new RedisLockProvider(
            Multiplexer(db.Object),
            new RedisLockOptions { FencingTokens = true, KeyPrefix = keyPrefix });

        await sut.TryAcquireFencedAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        return captured;
    }

    [Fact]
    public async Task A_counter_key_is_never_a_key_another_caller_could_lock()
    {
        // With an empty prefix the physical lock key IS the logical one, so the counter key has to live
        // somewhere no lock key can reach.
        var forA = await CaptureAcquireKeysAsync("a", string.Empty);
        var lockKey = forA[0];
        var counterKey = forA[1];

        Assert.Equal("a", lockKey.ToString());
        Assert.NotEqual(lockKey, counterKey);
        Assert.StartsWith(RedisFencingKeys.ReservedPrefix, counterKey.ToString(), StringComparison.Ordinal);

        // The collision that matters: a caller asking to lock a key of exactly that shape. Naming alone
        // cannot keep them apart when the caller controls the whole key, so the key is refused - the
        // counter namespace stays a namespace only counters occupy.
        await Assert.ThrowsAsync<ArgumentException>(
            () => CaptureAcquireKeysAsync(counterKey.ToString(), string.Empty));
    }

    [Fact]
    public async Task Two_logical_keys_never_share_a_counter()
    {
        var forA = (await CaptureAcquireKeysAsync("a", string.Empty))[1];
        var forB = (await CaptureAcquireKeysAsync("b", string.Empty))[1];

        Assert.NotEqual(forA, forB);
    }

    [Fact]
    public async Task A_lock_key_inside_the_reserved_counter_namespace_is_rejected_before_touching_redis()
    {
        // The one case the naming alone cannot separate: a caller with an empty prefix asking to lock a
        // key that IS in the counter namespace. Refusing it is what makes the separation a guarantee
        // rather than a hope - and it is a loud ArgumentException, not a lease quietly lost to a script
        // that half-ran.
        var db = new Mock<IDatabase>(MockBehavior.Strict);
        var sut = new RedisLockProvider(
            Multiplexer(db.Object),
            new RedisLockOptions { FencingTokens = true, KeyPrefix = string.Empty });

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => sut.TryAcquireFencedAsync(
                RedisFencingKeys.ReservedPrefix + "anything", "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.Contains(RedisFencingKeys.ReservedPrefix, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_prefix_puts_every_lock_key_out_of_reach_of_the_reserved_namespace()
    {
        // With the shipped prefix the guard can never fire: a physical lock key always starts with
        // "orionlock:", which is not the reserved counter prefix however the caller spells its key.
        var db = new Mock<IDatabase>();
        db.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)1L));
        var sut = new RedisLockProvider(Multiplexer(db.Object), new RedisLockOptions { FencingTokens = true });

        var taken = await sut.TryAcquireFencedAsync(
            RedisFencingKeys.ReservedPrefix + "anything", "owner-1", TimeSpan.FromSeconds(30), default);

        Assert.True(taken.Acquired);
    }

    [Fact]
    public async Task The_guard_does_not_apply_when_fencing_is_off_because_no_counter_key_exists()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        var sut = new RedisLockProvider(
            Multiplexer(db.Object), new RedisLockOptions { KeyPrefix = string.Empty });

        var taken = await sut.TryAcquireFencedAsync(
            RedisFencingKeys.ReservedPrefix + "anything", "owner-1", TimeSpan.FromSeconds(30), default);

        Assert.True(taken.Acquired);
        Assert.Null(taken.FencingToken);
    }
}
