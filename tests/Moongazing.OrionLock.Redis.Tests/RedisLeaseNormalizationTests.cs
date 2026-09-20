using Moongazing.OrionLock.Redis;
using Moq;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

/// <summary>
/// The exclusive Redis provider used to floor its lease with <c>(long)leaseDuration.TotalMilliseconds</c>,
/// so a positive sub-millisecond lease reached Redis as <c>0</c>. Zero is not a short lease there:
/// <c>PEXPIRE key 0</c> DELETES the key, so the first renewal would destroy the lock and hand it to any
/// waiter. The shared/exclusive sibling already normalized this; these tests pin the shared helper and
/// prove the exclusive provider actually routes through it. No server needed - the Redis surface is
/// substituted, so these run without Docker.
/// </summary>
public sealed class RedisLeaseNormalizationTests
{
    [Fact]
    public void ToMilliseconds_RoundsAPositiveSubMillisecondLeaseUpToOne()
    {
        Assert.Equal(1, RedisLease.ToMilliseconds(TimeSpan.FromTicks(1)));
        Assert.Equal(1, RedisLease.ToMilliseconds(TimeSpan.FromMilliseconds(0.4)));
        Assert.Equal(2, RedisLease.ToMilliseconds(TimeSpan.FromMilliseconds(1.2)));
        Assert.Equal(30_000, RedisLease.ToMilliseconds(TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ToMilliseconds_RejectsANonPositiveLease(int milliseconds)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => RedisLease.ToMilliseconds(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public async Task TryRenew_NeverSendsPexpireZero_ForASubMillisecondLease()
    {
        var db = new Mock<IDatabase>();
        RedisValue[] captured = [];
        db.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Callback((string _, RedisKey[] _, RedisValue[] values, CommandFlags _) => captured = values)
            .ReturnsAsync(RedisResult.Create((RedisValue)1L));

        var sut = new RedisLockProvider(Multiplexer(db.Object), new RedisLockOptions());

        Assert.True(await sut.TryRenewAsync("k", "owner-1", TimeSpan.FromTicks(1), default));
        Assert.Equal(1L, (long)captured[1]);
    }

    [Fact]
    public async Task TryAcquire_NeverSendsPxZero_ForASubMillisecondLease()
    {
        var db = new Mock<IDatabase>();
        TimeSpan? captured = null;
        db.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Callback((RedisKey _, RedisValue _, TimeSpan? expiry, bool _, When _, CommandFlags _) => captured = expiry)
            .ReturnsAsync(true);

        var sut = new RedisLockProvider(Multiplexer(db.Object), new RedisLockOptions());

        Assert.True(await sut.TryAcquireAsync("k", "owner-1", TimeSpan.FromTicks(1), default));
        Assert.Equal(TimeSpan.FromMilliseconds(1), captured);
    }

    [Fact]
    public async Task NonPositiveLease_IsACallerError_NotASilentlyBrokenLock()
    {
        var sut = new RedisLockProvider(Multiplexer(new Mock<IDatabase>().Object), new RedisLockOptions());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sut.TryAcquireAsync("k", "owner-1", TimeSpan.Zero, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sut.TryRenewAsync("k", "owner-1", TimeSpan.FromSeconds(-1), default));
    }

    [Fact]
    public async Task BlankKeyOrOwnerToken_IsRejected_BeforeTouchingRedis()
    {
        var db = new Mock<IDatabase>(MockBehavior.Strict);
        var sut = new RedisLockProvider(Multiplexer(db.Object), new RedisLockOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.TryAcquireAsync("  ", "owner-1", TimeSpan.FromSeconds(30), default));
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.TryRenewAsync("k", "", TimeSpan.FromSeconds(30), default));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.ReleaseAsync("", "owner-1", default));
    }

    [Fact]
    public async Task ACancelledCaller_DoesNotGoOnToTakeTheLock()
    {
        var db = new Mock<IDatabase>(MockBehavior.Strict);
        var sut = new RedisLockProvider(Multiplexer(db.Object), new RedisLockOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), cts.Token));
    }

    private static IConnectionMultiplexer Multiplexer(IDatabase db)
    {
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db);
        return mux.Object;
    }
}
