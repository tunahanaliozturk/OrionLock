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
        var l = NewLock(out _);
        await using var first = await l.TryAcquireAsync("k");
        var second = await l.TryAcquireAsync("k");
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
        var l = NewLock(out _);
        await using var first = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(() =>
            l.AcquireAsync("k", new DistributedLockOptions
            {
                WaitTimeout = TimeSpan.FromMilliseconds(400),
                RetryInterval = TimeSpan.FromMilliseconds(50),
                AutoRenew = false,
            }));
        sw.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 350, 2000);
    }

    [Fact]
    public async Task Acquire_ShouldSucceed_WhenLockFreesBeforeWaitTimeout()
    {
        var l = NewLock(out _);
        var first = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        var release = Task.Run(async () => { await Task.Delay(150); await first.DisposeAsync(); });
        await using var second = await l.AcquireAsync("k", new DistributedLockOptions
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
        var l = NewLock(out _);
        await using var first = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            l.AcquireAsync("k", new DistributedLockOptions { WaitTimeout = TimeSpan.FromSeconds(30) }, cts.Token));
    }
}
