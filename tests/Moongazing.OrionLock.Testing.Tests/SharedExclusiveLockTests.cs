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
        // Determinism note: the watchdog renews at LeaseDuration/3 and a non-renewed lease lapses after
        // LeaseDuration, so the slack a renewal may slip by before the hold dies is (2/3 * LeaseDuration).
        // With a 120ms lease that slack was only ~80ms, which a slow/overloaded CI runner could blow
        // past between two Task.Delay-driven renewals - the in-memory provider then prunes the expired
        // owner, TryRenewAsync returns false, the handle Surrenders, and IsHeld flips, flaking this
        // assertion. A 1000ms lease widens the renewal slack to ~667ms (an order of magnitude more CI
        // scheduling tolerance) while staying short enough that, had auto-renew NOT fired, the lease
        // would have lapsed well within the 1500ms observation window - so the test still genuinely
        // proves renewal rather than merely outrunning the clock.
        var opts = new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(1000),
            WaitTimeout = TimeSpan.FromMilliseconds(200),
            RetryInterval = TimeSpan.FromMilliseconds(10),
            AutoRenew = true,
        };
        await using var x = await locker.AcquireExclusiveAsync("k", opts);
        await Task.Delay(1500);   // well past the 1000ms lease; watchdog (renews ~every 333ms) kept it alive
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

        // Absolute durations are scaled up (vs an earlier 250ms/400ms pairing) so that CI scheduler
        // jitter is small RELATIVE to the values, while the gap between a correct run and a buggy one
        // stays large. A correct clamp returns at ~WaitTimeout (1000ms); an UNCLAMPED implementation
        // would sleep a full 1600ms retry interval before observing the timeout, landing at ~2600ms.
        var opts = new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            WaitTimeout = TimeSpan.FromMilliseconds(1000),
            // A retry interval larger than the wait budget would, unclamped, overshoot by up to one
            // full interval before the timeout was observed.
            RetryInterval = TimeSpan.FromMilliseconds(1600),
            AutoRenew = false,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(
            () => locker.AcquireExclusiveAsync("k", opts));
        sw.Stop();

        // The clamped delay must keep total wait close to WaitTimeout, not WaitTimeout + interval. The
        // 600ms margin absorbs CI jitter yet still sits far below the ~2600ms an unclamped overshoot
        // would reach, so the assertion keeps its power to catch the regression it guards.
        Assert.True(
            sw.Elapsed < opts.WaitTimeout + TimeSpan.FromMilliseconds(600),
            $"blocking wait {sw.ElapsedMilliseconds}ms overshot WaitTimeout {opts.WaitTimeout.TotalMilliseconds}ms by more than the allowed margin");
    }

    [Fact]
    public async Task NullOrWhitespaceKey_Throws()
    {
        var locker = NewLock();
        await Assert.ThrowsAsync<ArgumentException>(() => locker.AcquireSharedAsync("  ", Fast()));
        await Assert.ThrowsAsync<ArgumentException>(() => locker.TryAcquireExclusiveAsync("", Fast()));
    }

    // ---- v0.5.0 TryAcquire-with-deadline ----------------------------------------------------

    [Fact]
    public async Task TryAcquireSharedWithDeadline_Succeeds_WhenFree()
    {
        var locker = NewLock();
        await using var handle = await locker.TryAcquireSharedAsync("k", TimeSpan.FromSeconds(5), Fast());
        Assert.NotNull(handle);
        Assert.True(handle!.IsHeld);
    }

    [Fact]
    public async Task TryAcquireExclusiveWithDeadline_ReturnsNull_OnDeadline_NotThrows()
    {
        var locker = NewLock();
        await using var reader = await locker.AcquireSharedAsync("k", Fast());

        // A writer cannot get in while the reader holds. The deadline overload must give up by returning
        // null rather than throwing LockAcquisitionTimeoutException (the block-or-throw AcquireExclusive
        // behaviour) - that is the whole point of the acquire-or-give-up surface.
        var result = await locker.TryAcquireExclusiveAsync("k", TimeSpan.FromMilliseconds(150), Fast());
        Assert.Null(result);
    }

    [Fact]
    public async Task TryAcquireExclusiveWithDeadline_Succeeds_AfterReaderDrains()
    {
        var locker = NewLock();
        var reader = await locker.AcquireSharedAsync("k", Fast());

        // Give up generously, but drain the reader almost immediately so the poll loop wins well before
        // the deadline.
        var writerTask = locker.TryAcquireExclusiveAsync("k", TimeSpan.FromSeconds(5), Fast());
        await reader.DisposeAsync();

        await using var writer = await writerTask;
        Assert.NotNull(writer);
        Assert.True(writer!.IsHeld);
    }

    [Fact]
    public async Task TryAcquireWithDeadline_NonPositiveDeadline_IsSingleAttempt()
    {
        var locker = NewLock();
        await using var reader = await locker.AcquireSharedAsync("k", Fast());

        // A non-positive deadline performs exactly one attempt and gives up immediately (null), never
        // throwing. It cannot acquire because the reader holds.
        var result = await locker.TryAcquireExclusiveAsync("k", TimeSpan.Zero, Fast());
        Assert.Null(result);
    }

    [Fact]
    public async Task TryAcquireWithDeadline_Cancellation_Propagates()
    {
        var locker = NewLock();
        await using var reader = await locker.AcquireSharedAsync("k", Fast());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => locker.TryAcquireExclusiveAsync("k", TimeSpan.FromSeconds(30), Fast(), cts.Token));
    }

    [Fact]
    public async Task TryAcquireWithDeadline_DoesNotOvershoot_Deadline()
    {
        var locker = NewLock();
        await using var reader = await locker.AcquireSharedAsync("k", Fast());

        // Same clamp guarantee as the blocking acquire loop: a RetryInterval larger than the deadline
        // must not overshoot by a full interval. A correct clamp returns at ~deadline (1000ms); an
        // unclamped one would sleep a full 1600ms interval before giving up, landing at ~1600ms+.
        var opts = new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            WaitTimeout = TimeSpan.FromMilliseconds(200),
            RetryInterval = TimeSpan.FromMilliseconds(1600),
            AutoRenew = false,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await locker.TryAcquireExclusiveAsync("k", TimeSpan.FromMilliseconds(1000), opts);
        sw.Stop();

        Assert.Null(result);
        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(1000) + TimeSpan.FromMilliseconds(600),
            $"deadline acquire {sw.ElapsedMilliseconds}ms overshot the 1000ms deadline by more than the allowed margin");
    }
}
