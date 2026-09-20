using System.Diagnostics;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Redis;
using Moq;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

/// <summary>
/// v2.1: a contended Redis waiter subscribes to a release channel instead of issuing a
/// <c>SET NX</c> every retry interval.
/// </summary>
/// <remarks>
/// <para>
/// A DEDICATED channel, not keyspace notifications. Keyspace notifications need
/// <c>notify-keyspace-events</c> enabled on the server; it is off by default and a client library
/// cannot turn it on, so a lock that silently never notified would be worse than one that polls. A
/// channel the provider publishes to itself works on any Redis and costs one fire-and-forget
/// PUBLISH per release.
/// </para>
/// <para>
/// No server: the Redis surface is substituted, so these run without Docker and count exactly how
/// many round trips a waiter costs.
/// </para>
/// </remarks>
public sealed class RedisReleaseNotificationTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_release_that_actually_freed_the_key_announces_it_on_the_keys_channel()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)1L));
        var sub = new Mock<ISubscriber>();
        RedisChannel published = default;
        CommandFlags flags = CommandFlags.None;
        sub.Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Callback((RedisChannel c, RedisValue _, CommandFlags f) => { published = c; flags = f; })
            .ReturnsAsync(1L);

        var sut = new RedisLockProvider(Multiplexer(db.Object, sub.Object), new RedisLockOptions());

        await sut.ReleaseAsync("k", "owner-1", default);

        Assert.Equal(RedisChannel.Literal("orionlock:k:released"), published);
        // Fire-and-forget, so waking every waiter costs the releasing caller no round trip and a
        // failed notification can never fail a release.
        Assert.Equal(CommandFlags.FireAndForget, flags);
    }

    [Fact]
    public async Task A_release_that_freed_nothing_announces_nothing()
    {
        // The lease had already lapsed, or the key belongs to someone else. Waking waiters for a
        // hand-off that did not happen would send every one of them back to a refused attempt.
        var db = new Mock<IDatabase>();
        db.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)0L));
        var sub = new Mock<ISubscriber>(MockBehavior.Strict);

        var sut = new RedisLockProvider(Multiplexer(db.Object, sub.Object), new RedisLockOptions());

        await sut.ReleaseAsync("k", "owner-1", default);

        sub.Verify(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task A_waiter_parks_on_the_channel_instead_of_spinning_on_SET_NX()
    {
        // The round-trip count is the whole point: one refused SET NX, then nothing at all until
        // the release arrives, then one winning SET NX. Polling would have issued a SET NX every
        // 10 ms for the 400 ms this waiter is parked - forty of them.
        var db = new Mock<IDatabase>();
        var attempts = 0;
        db.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => Interlocked.Increment(ref attempts) > 1);
        db.Setup(d => d.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(TimeSpan.FromSeconds(30));

        var (sub, fireRelease) = SubscriberCapturingItsHandler();
        var sut = new RedisLockProvider(Multiplexer(db.Object, sub), new RedisLockOptions());

        var waiter = sut.WaitForAcquireAsync(
            "k", "owner-1", Lease, TimeSpan.FromSeconds(30),
            new LockWaitPolicy(TimeSpan.FromMilliseconds(10)), default);

        await Task.Delay(400);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref attempts));

        fireRelease();

        Assert.True((await waiter).Acquired);
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task A_waiter_whose_budget_runs_out_returns_false_and_unsubscribes()
    {
        var db = DatabaseThatNeverGrants();
        var (sub, _) = SubscriberCapturingItsHandler();
        var sut = new RedisLockProvider(Multiplexer(db.Object, sub), new RedisLockOptions());

        var sw = Stopwatch.StartNew();
        var acquired = await sut.WaitForAcquireAsync(
            "k", "owner-1", Lease, TimeSpan.FromMilliseconds(250), LockWaitPolicy.Default, default);
        sw.Stop();

        Assert.False(acquired.Acquired);
        Assert.InRange(sw.ElapsedMilliseconds, 200, 5000);
        Mock.Get(sub).Verify(
            s => s.UnsubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task A_cancelled_waiter_leaves_no_subscription_behind()
    {
        var db = DatabaseThatNeverGrants();
        var (sub, _) = SubscriberCapturingItsHandler();
        var sut = new RedisLockProvider(Multiplexer(db.Object, sub), new RedisLockOptions());
        using var cts = new CancellationTokenSource(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.WaitForAcquireAsync(
            "k", "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, cts.Token));

        Mock.Get(sub).Verify(
            s => s.UnsubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task A_waiter_still_wakes_for_a_lease_that_lapses_without_anyone_releasing_it()
    {
        // A crashed holder publishes nothing. The wait is therefore also bounded by the holder's
        // remaining TTL, so an expiry costs one extra wake - not one poll per retry interval.
        var db = new Mock<IDatabase>();
        var attempts = 0;
        db.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => Interlocked.Increment(ref attempts) > 1);
        db.Setup(d => d.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(TimeSpan.FromMilliseconds(200));

        var (sub, _) = SubscriberCapturingItsHandler();
        var sut = new RedisLockProvider(Multiplexer(db.Object, sub), new RedisLockOptions());

        var acquired = await sut.WaitForAcquireAsync(
            "k", "owner-1", Lease, TimeSpan.FromSeconds(30), LockWaitPolicy.Default, default);

        Assert.True(acquired.Acquired);
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task A_finite_waiter_does_not_sleep_through_a_lock_that_is_already_free()
    {
        // The lease lapsed between the failed SET NX and the TTL read, so Redis reports no TTL and
        // nothing is ever going to be published for it - a TTL expiry publishes nothing. Parking on
        // the channel here burns the caller's whole budget with the key free the entire time.
        // Before the fix this retried at once ONLY for an infinite wait, which is backwards: the
        // finite waiter is the one with a budget to lose.
        var db = new Mock<IDatabase>();
        var attempts = 0;
        db.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => Interlocked.Increment(ref attempts) > 1);
        db.Setup(d => d.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((TimeSpan?)null);

        var (sub, _) = SubscriberCapturingItsHandler();
        var sut = new RedisLockProvider(Multiplexer(db.Object, sub), new RedisLockOptions());

        var sw = Stopwatch.StartNew();
        var acquired = await sut.WaitForAcquireAsync(
            "k", "owner-1", Lease, TimeSpan.FromSeconds(10), LockWaitPolicy.Default, default);
        sw.Stop();

        Assert.True(acquired.Acquired);
        Assert.Equal(2, Volatile.Read(ref attempts));
        // The old code slept the full ten-second budget here.
        Assert.True(sw.ElapsedMilliseconds < 1000, $"the waiter parked for {sw.ElapsedMilliseconds}ms on a free lock");
    }

    [Fact]
    public async Task A_key_that_never_reports_a_TTL_is_not_spun_on()
    {
        // The other thing a null TTL means: StackExchange.Redis cannot tell "no key" from "key with
        // no expiry". A foreign persistent key under our prefix would otherwise be retried in a
        // tight loop for the whole budget, at full CPU.
        var db = new Mock<IDatabase>();
        var attempts = 0;
        db.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(() => { Interlocked.Increment(ref attempts); return false; });
        db.Setup(d => d.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync((TimeSpan?)null);

        var (sub, _) = SubscriberCapturingItsHandler();
        var sut = new RedisLockProvider(Multiplexer(db.Object, sub), new RedisLockOptions());

        var acquired = await sut.WaitForAcquireAsync(
            "k", "owner-1", Lease, TimeSpan.FromMilliseconds(300),
            new LockWaitPolicy(TimeSpan.FromMilliseconds(50)), default);

        Assert.False(acquired.Acquired);
        // One immediate retry, then the poll floor: roughly 300 / 50 attempts, nowhere near a spin.
        Assert.InRange(Volatile.Read(ref attempts), 2, 20);
    }

    [Fact]
    public async Task Turning_notifications_off_restores_the_poll_loop_exactly()
    {
        // The escape hatch for a deployment where pub/sub is unavailable: no subscribe at all, and
        // the waiter is back to a SET NX every retry interval.
        var db = DatabaseThatNeverGrants();
        var sub = new Mock<ISubscriber>(MockBehavior.Strict);
        var options = new RedisLockOptions { UseReleaseNotifications = false };
        var sut = new RedisLockProvider(Multiplexer(db.Object, sub.Object), options);

        var acquired = await sut.WaitForAcquireAsync(
            "k", "owner-1", Lease, TimeSpan.FromMilliseconds(200),
            new LockWaitPolicy(TimeSpan.FromMilliseconds(20)), default);

        Assert.False(acquired.Acquired);
        db.Verify(d => d.StringSetAsync(
            It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.AtLeast(2));
    }

    private static Mock<IDatabase> DatabaseThatNeverGrants()
    {
        var db = new Mock<IDatabase>();
        db.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(false);
        db.Setup(d => d.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(TimeSpan.FromSeconds(30));
        return db;
    }

    /// <summary>
    /// A subscriber that hands back the handler the provider registered, so a test can deliver a
    /// release notification the way a real Redis would.
    /// </summary>
    private static (ISubscriber Subscriber, Action FireRelease) SubscriberCapturingItsHandler()
    {
        var sub = new Mock<ISubscriber>();
        Action<RedisChannel, RedisValue>? handler = null;
        RedisChannel subscribed = default;
        sub.Setup(s => s.SubscribeAsync(
                It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .Callback((RedisChannel c, Action<RedisChannel, RedisValue> h, CommandFlags _) =>
            {
                subscribed = c;
                handler = h;
            })
            .Returns(Task.CompletedTask);
        sub.Setup(s => s.UnsubscribeAsync(
                It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
            .Returns(Task.CompletedTask);

        return (sub.Object, () => handler?.Invoke(subscribed, "released"));
    }

    private static IConnectionMultiplexer Multiplexer(IDatabase db, ISubscriber sub)
    {
        var mux = new Mock<IConnectionMultiplexer>();
        mux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db);
        mux.Setup(m => m.GetSubscriber(It.IsAny<object>())).Returns(sub);
        return mux.Object;
    }
}
