using System.Diagnostics;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Redis;
using Moongazing.OrionLock.Tests.Containers;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

/// <summary>
/// The release-channel wait against a real Redis. The substituted-surface suite proves the waiter
/// parks and counts the round trips; this proves the channel name, the PUBLISH and the subscribe
/// actually line up on a server, which a mock can always be made to agree with.
/// </summary>
public sealed class RedisReleaseNotificationContainerTests : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private readonly RedisContainer container = new RedisBuilder(ContainerImages.Redis).Build();
    private IConnectionMultiplexer mux = default!;

    public async Task InitializeAsync()
        => mux = await RedisContainerStartup.StartAndConnectAsync(container).ConfigureAwait(false);

    public async Task DisposeAsync()
    {
        await mux.DisposeAsync();
        await container.DisposeAsync();
    }

    private RedisLockProvider NewProvider() => new(mux, new RedisLockOptions());

    [DockerFact]
    public async Task A_waiter_is_handed_the_lock_the_moment_the_holder_releases()
    {
        var sut = NewProvider();
        var key = $"released-{Guid.NewGuid():N}";

        Assert.True(await sut.TryAcquireAsync(key, "holder", Lease, default));

        var sw = Stopwatch.StartNew();
        // A five-second retry interval: if the waiter were polling it could not possibly come back
        // in the ~300 ms the holder takes to release, so the timing IS the assertion.
        var waiter = sut.WaitForAcquireAsync(
            key, "waiter", Lease, TimeSpan.FromSeconds(30), new LockWaitPolicy(TimeSpan.FromSeconds(5)), default);

        await Task.Delay(300);
        await sut.ReleaseAsync(key, "holder", default);

        Assert.True((await waiter).Acquired);
        sw.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 250, 4000);

        await sut.ReleaseAsync(key, "waiter", default);
    }

    [DockerFact]
    public async Task A_wait_that_outlives_its_budget_returns_false_without_taking_the_lock()
    {
        var sut = NewProvider();
        var key = $"released-{Guid.NewGuid():N}";

        Assert.True(await sut.TryAcquireAsync(key, "holder", Lease, default));

        var acquired = await sut.WaitForAcquireAsync(
            key, "waiter", Lease, TimeSpan.FromMilliseconds(500), LockWaitPolicy.Default, default);

        Assert.False(acquired.Acquired);
        await sut.ReleaseAsync(key, "holder", default);
    }

    [DockerFact]
    public async Task A_waiter_still_wakes_for_a_lease_that_lapses_with_nobody_to_publish_it()
    {
        // The crashed-holder case: the key goes away on its TTL and nothing is ever published.
        var sut = NewProvider();
        var key = $"released-{Guid.NewGuid():N}";

        Assert.True(await sut.TryAcquireAsync(key, "holder", TimeSpan.FromMilliseconds(400), default));

        var acquired = await sut.WaitForAcquireAsync(
            key, "waiter", Lease, TimeSpan.FromSeconds(20), new LockWaitPolicy(TimeSpan.FromSeconds(5)), default);

        Assert.True(acquired.Acquired);
        await sut.ReleaseAsync(key, "waiter", default);
    }

    [DockerFact]
    public async Task Many_waiters_on_one_key_are_all_woken_and_all_eventually_win()
    {
        var sut = NewProvider();
        var key = $"released-{Guid.NewGuid():N}";
        var holder = new DistributedLock(sut);
        var options = new DistributedLockOptions
        {
            LeaseDuration = Lease,
            WaitTimeout = TimeSpan.FromSeconds(60),
            RetryInterval = TimeSpan.FromMilliseconds(200),
            AutoRenew = false,
        };

        var held = await holder.TryAcquireAsync(key, options);
        Assert.NotNull(held);

        var winners = 0;
        var tasks = new Task[16];
        for (var i = 0; i < tasks.Length; i++)
        {
            var waiter = new DistributedLock(sut);
            tasks[i] = Task.Run(async () =>
            {
                await using var h = await waiter.AcquireAsync(key, options);
                Interlocked.Increment(ref winners);
            });
        }

        await Task.Delay(300);
        await held!.DisposeAsync();
        await Task.WhenAll(tasks);

        Assert.Equal(16, winners);
    }
}
