using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Tests;

/// <summary>
/// An undisposed handle never stopped. The watchdog task roots the handle, so a forgotten
/// <c>await using</c> renewed its lease forever: the lock was never released, no other process could
/// take the key, and on SQL Server and PostgreSQL a dedicated open connection stayed pinned for the
/// life of the process. Nothing noticed, because renewal kept succeeding.
/// </summary>
public sealed class MaxHoldDurationTests
{
    /// <summary>Counts renewals and releases so an abandoned handle can be watched from outside.</summary>
    private sealed class CountingProvider : IDistributedLockProvider
    {
        private int renewals;
        private int releases;

        public int Renewals => Volatile.Read(ref renewals);

        public int Releases => Volatile.Read(ref releases);

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref renewals);
            return Task.FromResult(true);
        }

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref releases);
            return Task.CompletedTask;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task AnUndisposedHandle_StopsRenewing_AndReleases_OnceMaxHoldElapses()
    {
        var provider = new CountingProvider();
        var sut = new DistributedLock(provider);

        // Deliberately NOT disposed: this is the forgotten `await using`.
        var handle = await sut.AcquireAsync("k", new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(90),  // renews every 30 ms
            MaxHoldDuration = TimeSpan.FromMilliseconds(150),
        });

        await WaitUntilAsync(() => !handle.IsHeld, TimeSpan.FromSeconds(5));

        Assert.False(handle.IsHeld);
        Assert.True(handle.LostToken.IsCancellationRequested);
        // Released, so the key comes back even on a session-scoped backend where merely not renewing
        // would free nothing and would leave a connection pinned.
        Assert.Equal(1, provider.Releases);

        // And renewal really stopped: the count must not keep climbing.
        var renewalsAtSurrender = provider.Renewals;
        await Task.Delay(300);
        Assert.Equal(renewalsAtSurrender, provider.Renewals);
    }

    [Fact]
    public async Task TheDefaultIsTenLeases_SoAnOrdinaryHoldIsUnaffected()
    {
        var options = new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) };
        Assert.Null(options.MaxHoldDuration);

        var provider = new CountingProvider();
        await using var handle = await new DistributedLock(provider).AcquireAsync("k", new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(60),
        });

        // 10 x 60 ms = 600 ms, so a hold that renews a few times inside 200 ms is untouched.
        await WaitUntilAsync(() => provider.Renewals >= 2, TimeSpan.FromSeconds(5));
        Assert.True(handle.IsHeld);
    }

    [Fact]
    public void AMaxHoldShorterThanOneLease_IsRejected()
    {
        // It would give up before renewing even once: an immediate surrender, not a leak backstop.
        var sut = new DistributedLock(new CountingProvider());

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = sut.AcquireAsync("k", new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            MaxHoldDuration = TimeSpan.FromSeconds(29),
        }); });
    }
}
