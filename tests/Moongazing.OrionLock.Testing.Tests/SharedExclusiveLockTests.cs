using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Testing.Tests;

public class SharedExclusiveLockTests
{
    private static SharedExclusiveLock NewLock() =>
        new(new InMemorySharedExclusiveLockProvider());

    private static DistributedLockOptions Fast(TimeSpan? wait = null) => new()
    {
        LeaseDuration = TimeSpan.FromSeconds(30),
        WaitTimeout = wait ?? TimeSpan.FromMilliseconds(200),
        RetryInterval = TimeSpan.FromMilliseconds(10),
        AutoRenew = false,
    };

    [Fact]
    public async Task TryShared_ManyHolders_Coexist()
    {
        var locker = NewLock();
        await using var a = await locker.TryAcquireSharedAsync("k", Fast());
        await using var b = await locker.TryAcquireSharedAsync("k", Fast());
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a!.IsHeld);
        Assert.True(b!.IsHeld);
    }

    [Fact]
    public async Task TryExclusive_Null_WhileSharedHeld()
    {
        var locker = NewLock();
        await using var s = await locker.TryAcquireSharedAsync("k", Fast());
        Assert.NotNull(s);
        Assert.Null(await locker.TryAcquireExclusiveAsync("k", Fast()));
    }

    [Fact]
    public async Task TryShared_Null_WhileExclusiveHeld()
    {
        var locker = NewLock();
        await using var x = await locker.TryAcquireExclusiveAsync("k", Fast());
        Assert.NotNull(x);
        Assert.Null(await locker.TryAcquireSharedAsync("k", Fast()));
    }

    [Fact]
    public async Task AcquireExclusive_Throws_WhenSharedNeverDrains()
    {
        var locker = NewLock();
        await using var s = await locker.AcquireSharedAsync("k", Fast());
        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(
            () => locker.AcquireExclusiveAsync("k", Fast(TimeSpan.FromMilliseconds(150))));
    }

    [Fact]
    public async Task AcquireExclusive_Succeeds_AfterSharedDrains()
    {
        var locker = NewLock();
        var reader = await locker.AcquireSharedAsync("k", Fast());

        var writerTask = locker.AcquireExclusiveAsync("k", Fast(TimeSpan.FromSeconds(5)));
        Assert.False(writerTask.IsCompleted);

        await reader.DisposeAsync();   // drain the only reader

        await using var writer = await writerTask;
        Assert.True(writer.IsHeld);
        Assert.Equal("k", writer.Key);
    }

    [Fact]
    public async Task AcquireShared_Succeeds_AfterExclusiveReleased()
    {
        var locker = NewLock();
        var writer = await locker.AcquireExclusiveAsync("k", Fast());

        var readerTask = locker.AcquireSharedAsync("k", Fast(TimeSpan.FromSeconds(5)));
        Assert.False(readerTask.IsCompleted);

        await writer.DisposeAsync();

        await using var reader = await readerTask;
        Assert.True(reader.IsHeld);
    }

    [Fact]
    public async Task Dispose_ReleasesExclusive_AllowingReacquire()
    {
        var locker = NewLock();
        var x = await locker.AcquireExclusiveAsync("k", Fast());
        await x.DisposeAsync();
        await using var again = await locker.TryAcquireExclusiveAsync("k", Fast());
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Dispose_ReleasesShared_AllowingWriter()
    {
        var locker = NewLock();
        var s = await locker.AcquireSharedAsync("k", Fast());
        await s.DisposeAsync();
        await using var writer = await locker.TryAcquireExclusiveAsync("k", Fast());
        Assert.NotNull(writer);
    }

    [Fact]
    public async Task Acquire_Cancellation_Propagates()
    {
        var locker = NewLock();
        await using var s = await locker.AcquireSharedAsync("k", Fast());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => locker.AcquireExclusiveAsync("k", Fast(TimeSpan.FromSeconds(30)), cts.Token));
    }

    [Fact]
    public async Task AutoRenew_KeepsExclusiveAlive_BeyondLease()
    {
        var locker = NewLock();
        var opts = new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(120),
            WaitTimeout = TimeSpan.FromMilliseconds(200),
            RetryInterval = TimeSpan.FromMilliseconds(10),
            AutoRenew = true,
        };
        await using var x = await locker.AcquireExclusiveAsync("k", opts);
        await Task.Delay(300);   // well past the 120ms lease; watchdog should have renewed
        Assert.True(x.IsHeld);
        // A reader must still be blocked because the lease was renewed, not expired.
        Assert.Null(await locker.TryAcquireSharedAsync("k", Fast()));
    }

    [Fact]
    public async Task ContendedExclusive_AfterRelease_NewSharedAcquiresImmediately()
    {
        var locker = NewLock();
        var reader = await locker.AcquireSharedAsync("k", Fast());

        // Blocking exclusive acquire that has to retry (reader present). With a stable owner token,
        // the successful retry clears the writer's own reservation.
        var writerTask = locker.AcquireExclusiveAsync("k", Fast(TimeSpan.FromSeconds(5)));
        await reader.DisposeAsync();   // drain the only reader so the retrying writer can win
        var writer = await writerTask;
        Assert.True(writer.IsHeld);

        await writer.DisposeAsync();

        // No stale reservation must linger: a new shared acquire succeeds right away (TryAcquire,
        // single attempt - it would return null if a stale reservation were still denying readers).
        await using var s = await locker.TryAcquireSharedAsync("k", Fast());
        Assert.NotNull(s);
        Assert.True(s!.IsHeld);
    }

    [Fact]
    public async Task BlockingWait_DoesNotOvershoot_WaitTimeout()
    {
        var locker = NewLock();
        await using var s = await locker.AcquireSharedAsync("k", Fast());

        var opts = new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            WaitTimeout = TimeSpan.FromMilliseconds(250),
            // A retry interval larger than the wait budget would, unclamped, overshoot by up to one
            // full interval before the timeout was observed.
            RetryInterval = TimeSpan.FromMilliseconds(400),
            AutoRenew = false,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(
            () => locker.AcquireExclusiveAsync("k", opts));
        sw.Stop();

        // The clamped delay must keep total wait close to WaitTimeout, not WaitTimeout + interval.
        Assert.True(
            sw.Elapsed < opts.WaitTimeout + TimeSpan.FromMilliseconds(200),
            $"blocking wait {sw.ElapsedMilliseconds}ms overshot WaitTimeout {opts.WaitTimeout.TotalMilliseconds}ms by more than the allowed margin");
    }

    [Fact]
    public async Task NullOrWhitespaceKey_Throws()
    {
        var locker = NewLock();
        await Assert.ThrowsAsync<ArgumentException>(() => locker.AcquireSharedAsync("  ", Fast()));
        await Assert.ThrowsAsync<ArgumentException>(() => locker.TryAcquireExclusiveAsync("", Fast()));
    }
}
