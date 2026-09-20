using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class ReentrancyTests
{
    [Fact]
    public async Task ReAcquire_SameKey_ShouldNotTouchBackend_AndShouldSucceed()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        await using var outer = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        await using var inner = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        Assert.Equal("k", inner.Key);
        Assert.Equal(1, provider.AcquireCount);
    }

    [Fact]
    public async Task OuterDispose_ShouldReleaseBackend_OnlyAfterInnerDisposed()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        var outer = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        var inner = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        await inner.DisposeAsync();
        Assert.Equal(0, provider.ReleaseCount);

        await outer.DisposeAsync();
        Assert.Equal(1, provider.ReleaseCount);
    }

    [Fact]
    public async Task TryAcquire_DifferentKey_ShouldHitBackend()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        await using var a = await l.AcquireAsync("k1", new DistributedLockOptions { AutoRenew = false });
        await using var b = await l.AcquireAsync("k2", new DistributedLockOptions { AutoRenew = false });

        Assert.Equal(2, provider.AcquireCount);
    }

    [Fact]
    public async Task ReAcquire_AfterFullRelease_ShouldHitBackendAgain()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        await (await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false })).DisposeAsync();
        await (await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false })).DisposeAsync();

        Assert.Equal(2, provider.AcquireCount);
    }

    [Fact]
    public async Task ReAcquire_SameFlow_AcrossAnAwait_ShouldStillCollapse()
    {
        // The owner scope lives in an AsyncLocal established at the outermost acquire. It has to
        // survive the caller's own awaits, including a re-entry from a nested async method, or
        // reentrancy would only work for a straight-line critical section.
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        await using var outer = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        await Task.Yield();
        await using var inner = await NestedAcquireAsync(l);

        Assert.Equal(1, provider.AcquireCount);

        static async Task<IDistributedLockHandle> NestedAcquireAsync(IDistributedLock l)
        {
            await Task.Delay(1);
            return await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        }
    }

    [Fact]
    public async Task TwoConcurrentUnrelatedCallers_OnOneInstance_ShouldNotShareOneLease()
    {
        // The regression this pins: IDistributedLock is a DI singleton, so two unrelated requests hit
        // ONE DistributedLock instance. Keyed on the lock key alone, the second caller was handed a
        // nested handle over the first caller's lease without the backend ever being asked - both
        // inside the critical section at once. Note the single lock instance; the pre-existing
        // mutual-exclusion tests use two instances, which is exactly why this was invisible.
        var provider = new InMemoryLockProvider();
        var l = new DistributedLock(provider);
        var options = new DistributedLockOptions { AutoRenew = false, LeaseDuration = TimeSpan.FromSeconds(30) };

        // Handshake so the two holds provably overlap instead of relying on scheduling luck.
        var firstIsInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHasTried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> FirstAsync()
        {
            var handle = await l.TryAcquireAsync("k", options);
            firstIsInside.SetResult();
            if (handle is null)
            {
                return false;
            }

            await secondHasTried.Task;       // stay inside the critical section while the other caller tries
            await handle.DisposeAsync();
            return true;
        }

        async Task<bool> SecondAsync()
        {
            await firstIsInside.Task;
            var handle = await l.TryAcquireAsync("k", options);
            secondHasTried.SetResult();
            if (handle is null)
            {
                return false;
            }

            await handle.DisposeAsync();
            return true;
        }

        // SuppressFlow makes each task a genuinely unrelated flow rather than a descendant of this
        // test's execution context - the in-process equivalent of two independent HTTP requests.
        Task<bool> first, second;
        using (ExecutionContext.SuppressFlow())
        {
            first = Task.Run(FirstAsync);
            second = Task.Run(SecondAsync);
        }

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(results[0], "the first caller should have taken the lease");
        Assert.False(results[1], "the second, unrelated caller must be refused while the first holds it");
    }

    [Fact]
    public async Task AfterAWarmUpAcquireAndRelease_ForkedTasks_ShouldNotShareOneLease()
    {
        // The owner identity must belong to the ACQUISITION, not to the flow. A flow-lifetime identity
        // outlives the hold: warm up once, and every task forked afterwards inherits the same identity,
        // so the second one matches the first one's registry entry and is handed a nested handle with
        // the backend never consulted - the mutual-exclusion hole again, one release later.
        var provider = new InMemoryLockProvider();
        var l = new DistributedLock(provider);
        var options = new DistributedLockOptions { AutoRenew = false, LeaseDuration = TimeSpan.FromSeconds(30) };

        // Warm-up: this flow acquires and fully releases "k", so it carries whatever ambient state an
        // acquire leaves behind.
        await (await l.AcquireAsync("k", options)).DisposeAsync();

        var firstIsInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHasTried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> FirstAsync()
        {
            var handle = await l.TryAcquireAsync("k", options);
            firstIsInside.SetResult();
            if (handle is null)
            {
                return false;
            }

            await secondHasTried.Task;
            await handle.DisposeAsync();
            return true;
        }

        async Task<bool> SecondAsync()
        {
            await firstIsInside.Task;
            var handle = await l.TryAcquireAsync("k", options);
            secondHasTried.SetResult();
            if (handle is null)
            {
                return false;
            }

            await handle.DisposeAsync();
            return true;
        }

        // Deliberately NOT suppressing flow: these two tasks are forked from a flow that has already
        // acquired and released the key, which is exactly the case that regressed.
        var first = Task.Run(FirstAsync);
        var second = Task.Run(SecondAsync);

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(results[0], "the first forked task should have taken the lease");
        Assert.False(results[1], "the second forked task must not inherit the first one's lease");
    }

    [Fact]
    public async Task NestedAcquire_ShouldBeRefused_OnceTheRealLeaseIsLost()
    {
        // The watchdog can surrender the lease mid-flow. A nested handle minted over that dead lease
        // would report IsHeld=false but still let the caller believe it had re-entered a held lock,
        // and no backend acquire would ever be attempted.
        var provider = new LosingProvider();
        var l = new DistributedLock(provider);

        await using var outer = await l.AcquireAsync(
            "k",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(60), AutoRenew = true });

        await WaitUntilAsync(() => !outer.IsHeld);
        Assert.False(outer.IsHeld);
        Assert.Equal(1, Volatile.Read(ref provider.AcquireCount));

        // Same flow, same key - but the lease is gone, so this must go to the backend.
        await using var second = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        Assert.Equal(2, Volatile.Read(ref provider.AcquireCount));
        Assert.True(second.IsHeld);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>Grants every acquire but never renews, so the watchdog surrenders the lease.</summary>
    private sealed class LosingProvider : Moongazing.OrionLock.Providers.IDistributedLockProvider
    {
        public int AcquireCount;

        public Task<bool> TryAcquireAsync(string k, string o, TimeSpan d, CancellationToken c)
        {
            Interlocked.Increment(ref AcquireCount);
            return Task.FromResult(true);
        }

        public Task<bool> TryRenewAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(false);

        public Task ReleaseAsync(string k, string o, CancellationToken c) => Task.CompletedTask;
    }

    private sealed class CountingProvider : Moongazing.OrionLock.Providers.IDistributedLockProvider
    {
        public int AcquireCount;
        public int ReleaseCount;

        public Task<bool> TryAcquireAsync(string k, string o, TimeSpan d, CancellationToken c)
        {
            Interlocked.Increment(ref AcquireCount);
            return Task.FromResult(true);
        }

        public Task<bool> TryRenewAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(true);

        public Task ReleaseAsync(string k, string o, CancellationToken c)
        {
            Interlocked.Increment(ref ReleaseCount);
            return Task.CompletedTask;
        }
    }
}
