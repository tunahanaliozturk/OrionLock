namespace Moongazing.OrionLock.Tests.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;
using Xunit;

public sealed class LockEventObserverWireUpTests
{
    private sealed class CapturingObserver : ILockEventObserver
    {
        public readonly System.Collections.Generic.List<string> Events = new();
        public void OnAcquired(string key, double durationMs) { lock (Events) { Events.Add($"acquired:{key}"); } }
        public void OnAcquireTimedOut(string key, double waitMs) { lock (Events) { Events.Add($"timeout:{key}"); } }
        public void OnLeaseLost(string key) { lock (Events) { Events.Add($"lost:{key}"); } }
        public void OnReleased(string key) { lock (Events) { Events.Add($"released:{key}"); } }
    }

    [Fact]
    public async Task Acquire_and_dispose_fire_OnAcquired_then_OnReleased_via_DI_registration()
    {
        var observer = new CapturingObserver();
        var services = new ServiceCollection();
        services.AddSingleton<ILockEventObserver>(observer);
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var sut = sp.GetRequiredService<IDistributedLock>();

        var handle = await sut.AcquireAsync("wire-up-key",
            new DistributedLockOptions { LeaseDuration = System.TimeSpan.FromSeconds(5), AutoRenew = false });
        await handle.DisposeAsync();

        lock (observer.Events)
        {
            Assert.Contains("acquired:wire-up-key", observer.Events);
            Assert.Contains("released:wire-up-key", observer.Events);
        }
    }

    [Fact]
    public async Task Timeout_fires_OnAcquireTimedOut_via_DI_registration()
    {
        var observer = new CapturingObserver();
        var services = new ServiceCollection();
        services.AddSingleton<ILockEventObserver>(observer);
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var sut = sp.GetRequiredService<IDistributedLock>();

        // Hold the key with one handle, then try to acquire with a tiny timeout.
        var blocker = await sut.AcquireAsync("contested-key",
            new DistributedLockOptions { LeaseDuration = System.TimeSpan.FromSeconds(30), AutoRenew = false });
        try
        {
            // Second acquire must come from a DIFFERENT lock instance so reentrancy
            // does not short-circuit. Build a second provider-scoped container.
            var services2 = new ServiceCollection();
            services2.AddSingleton<ILockEventObserver>(observer);
            services2.AddOrionLock().UseInMemory();
            // Same in-memory provider instance is needed for contention; resolve the
            // provider from the FIRST container and re-register it in the second.
            var sharedProvider = sp.GetRequiredService<Moongazing.OrionLock.Providers.IDistributedLockProvider>();
            services2.AddSingleton(sharedProvider);
            await using var sp2 = services2.BuildServiceProvider();
            var contender = sp2.GetRequiredService<IDistributedLock>();

            await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(() => contender.AcquireAsync("contested-key",
                new DistributedLockOptions
                {
                    LeaseDuration = System.TimeSpan.FromSeconds(5),
                    WaitTimeout = System.TimeSpan.FromMilliseconds(150),
                    RetryInterval = System.TimeSpan.FromMilliseconds(50),
                    AutoRenew = false,
                }));
        }
        finally
        {
            await blocker.DisposeAsync();
        }

        lock (observer.Events)
        {
            Assert.Contains("timeout:contested-key", observer.Events);
        }
    }
}
