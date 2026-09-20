namespace Moongazing.OrionLock.Tests.Fencing;

using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;
using Xunit;

/// <summary>
/// An audit trail that records the fencing token can, after the fact, order two holders that both
/// believed they held the same key - the sequence a lease-based lock cannot prevent and therefore the
/// one worth being able to reconstruct.
/// </summary>
public sealed class FencingObserverTests
{
    private sealed class FencingObserver : ILockEventObserver
    {
        public readonly List<(string Key, long? Token)> Acquired = [];

        public void OnAcquired(string key, double durationMs)
            => throw new InvalidOperationException(
                "the pipeline must call the fencing-aware overload, not this one");

        public void OnAcquired(string key, double durationMs, long? fencingToken)
        {
            lock (Acquired) { Acquired.Add((key, fencingToken)); }
        }

        public void OnAcquireTimedOut(string key, double waitMs) { }
        public void OnLeaseLost(string key) { }
        public void OnReleased(string key) { }
    }

    /// <summary>An observer written before fencing existed: it overrides only the two-argument method.</summary>
    private sealed class LegacyObserver : ILockEventObserver
    {
        public readonly List<string> Acquired = [];

        public void OnAcquired(string key, double durationMs)
        {
            lock (Acquired) { Acquired.Add(key); }
        }

        public void OnAcquireTimedOut(string key, double waitMs) { }
        public void OnLeaseLost(string key) { }
        public void OnReleased(string key) { }
    }

    private static async Task<IDistributedLock> BuildAsync(ILockEventObserver observer, List<IAsyncDisposable> sinks)
    {
        var services = new ServiceCollection();
        services.AddSingleton(observer);
        services.AddOrionLock().UseInMemory();
        var sp = services.BuildServiceProvider();
        sinks.Add(sp);
        return await Task.FromResult(sp.GetRequiredService<IDistributedLock>());
    }

    [Fact]
    public async Task OnAcquired_receives_the_token_the_handle_carries()
    {
        var observer = new FencingObserver();
        var sinks = new List<IAsyncDisposable>();
        var sut = await BuildAsync(observer, sinks);

        await using (var handle = await sut.AcquireAsync(
            "observed-fence", new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5), AutoRenew = false }))
        {
            lock (observer.Acquired)
            {
                Assert.Equal(("observed-fence", handle.FencingToken), Assert.Single(observer.Acquired));
            }
        }

        foreach (var sink in sinks) { await sink.DisposeAsync(); }
    }

    [Fact]
    public async Task An_observer_written_before_fencing_still_gets_its_callback()
    {
        // The default interface member forwards, so adding the token to the contract did not break an
        // observer that never heard of it.
        var observer = new LegacyObserver();
        var sinks = new List<IAsyncDisposable>();
        var sut = await BuildAsync(observer, sinks);

        await using (await sut.AcquireAsync(
            "legacy-observer", new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5), AutoRenew = false }))
        {
        }

        lock (observer.Acquired)
        {
            Assert.Equal("legacy-observer", Assert.Single(observer.Acquired));
        }

        foreach (var sink in sinks) { await sink.DisposeAsync(); }
    }
}
