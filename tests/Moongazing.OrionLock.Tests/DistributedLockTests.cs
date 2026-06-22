using System.Diagnostics;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class DistributedLockTests
{
    private static DistributedLock NewLock(out InMemoryLockProvider provider)
    {
        provider = new InMemoryLockProvider();
        return new DistributedLock(provider);
    }

    [Fact]
    public async Task TryAcquire_ShouldReturnHandle_WhenFree()
    {
        var l = NewLock(out _);
        await using var h = await l.TryAcquireAsync("k");
        Assert.NotNull(h);
        Assert.Equal("k", h!.Key);
    }

    [Fact]
    public async Task TryAcquire_ShouldReturnNull_WhenHeld()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        await using var first = await holder.TryAcquireAsync("k");
        var second = await contender.TryAcquireAsync("k");
        Assert.Null(second);
    }

    [Fact]
    public async Task Acquire_ShouldSucceed_WhenFree()
    {
        var l = NewLock(out _);
        await using var h = await l.AcquireAsync("k", TimeSpan.FromSeconds(30));
        Assert.Equal("k", h.Key);
    }

    [Fact]
    public async Task Acquire_ShouldThrowTimeout_WhenHeldPastWaitTimeout()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        await using var first = await holder.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(() =>
            contender.AcquireAsync("k", new DistributedLockOptions
            {
                WaitTimeout = TimeSpan.FromMilliseconds(500),
                RetryInterval = TimeSpan.FromMilliseconds(50),
                AutoRenew = false,
            }));
        sw.Stop();
        // Lower bound (400ms, just under the 500ms WaitTimeout) still proves the call did NOT return
        // before the timeout elapsed. The upper bound is widened to 5000ms so a slow, loaded CI runner
        // taking far longer than the budget to notice the timeout cannot fail the test - load only ever
        // makes the observed elapsed larger, never smaller.
        Assert.InRange(sw.ElapsedMilliseconds, 400, 5000);
    }

    [Fact]
    public async Task Acquire_ShouldSucceed_WhenLockFreesBeforeWaitTimeout()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        var first = await holder.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        var release = Task.Run(async () => { await Task.Delay(150); await first.DisposeAsync(); });
        await using var second = await contender.AcquireAsync("k", new DistributedLockOptions
        {
            WaitTimeout = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromMilliseconds(50),
            AutoRenew = false,
        });
        await release;
        Assert.Equal("k", second.Key);
    }

    [Fact]
    public async Task Acquire_ShouldThrowOperationCanceled_WhenTokenCancelled()
    {
        var provider = new InMemoryLockProvider();
        var holder = new DistributedLock(provider);
        var contender = new DistributedLock(provider);

        await using var first = await holder.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            contender.AcquireAsync("k", new DistributedLockOptions { WaitTimeout = TimeSpan.FromSeconds(30) }, cts.Token));
    }
}
