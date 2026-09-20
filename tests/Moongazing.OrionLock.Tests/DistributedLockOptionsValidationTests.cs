using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

/// <summary>
/// <see cref="DistributedLockOptions"/> was never validated. A zero lease produced <c>SET ... PX 0</c>
/// on Redis - which is a DELETE, not a short lease - while throwing from deep inside the Redis and
/// PostgreSQL reader-writer providers; a negative one collapsed the renewal interval to its 10 ms floor
/// and hammered the backend for the life of the handle; a negative retry interval threw from inside
/// <c>Task.Delay</c> in the acquire loop; a zero grace period surrendered a good lease on the first
/// blip. All of them are now refused on the caller's own thread, where the value was set.
/// </summary>
public sealed class DistributedLockOptionsValidationTests
{
    private static DistributedLock NewLock() => new(new InMemoryLockProvider());

    private static SharedExclusiveLock NewRwLock() => new(new InMemorySharedExclusiveLockProvider());

    public static TheoryData<DistributedLockOptions> Invalid =>
    [
        new DistributedLockOptions { LeaseDuration = TimeSpan.Zero },
        new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(-1) },
        new DistributedLockOptions { WaitTimeout = TimeSpan.FromSeconds(-1) },
        new DistributedLockOptions { WaitTimeout = Timeout.InfiniteTimeSpan },
        new DistributedLockOptions { RetryInterval = TimeSpan.Zero },
        new DistributedLockOptions { RetryInterval = TimeSpan.FromMilliseconds(-1) },
        new DistributedLockOptions { RenewalFailureGracePeriod = TimeSpan.Zero },
        new DistributedLockOptions { RenewalFailureGracePeriod = TimeSpan.FromSeconds(-1) },
    ];

    [Theory]
    [MemberData(nameof(Invalid))]
    public void EveryExclusiveEntryPoint_RejectsBadOptions_OnTheCallersThread(DistributedLockOptions options)
    {
        var sut = NewLock();

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = sut.AcquireAsync("k", options); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = sut.TryAcquireAsync("k", options); });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = sut.TryAcquireAsync("k", TimeSpan.FromSeconds(1), options); });
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void EveryReaderWriterEntryPoint_RejectsBadOptions_OnTheCallersThread(DistributedLockOptions options)
    {
        var sut = NewRwLock();

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = sut.AcquireSharedAsync("k", options); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = sut.AcquireExclusiveAsync("k", options); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = sut.TryAcquireSharedAsync("k", options); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = sut.TryAcquireExclusiveAsync("k", options); });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = sut.TryAcquireSharedAsync("k", TimeSpan.FromSeconds(1), options); });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = sut.TryAcquireExclusiveAsync("k", TimeSpan.FromSeconds(1), options); });
    }

    [Fact]
    public async Task TheDefaultsAreValid_AndAZeroWaitTimeoutStillMeansOneAttempt()
    {
        var sut = NewLock();
        await using var held = await sut.AcquireAsync("k", new DistributedLockOptions());
        Assert.True(held.IsHeld);

        // WaitTimeout = Zero is a legitimate "try once, do not wait", so it must NOT be refused.
        var contender = new DistributedLock(new InMemoryLockProvider());
        await contender.AcquireAsync("free-key", new DistributedLockOptions { WaitTimeout = TimeSpan.Zero })
            .ContinueWith(t => t.Result.DisposeAsync(), TaskScheduler.Default);
    }

    [Fact]
    public async Task ARetryIntervalLongerThanTheWaitTimeout_IsStillAccepted_BecauseTheLoopClampsIt()
    {
        // Not a misconfiguration: the acquire loop clamps the poll delay to the remaining budget, so
        // this overshoots nothing. Pinning it here so the validator is never "tightened" into breaking it.
        var provider = new InMemoryLockProvider();
        await using var holder = await new DistributedLock(provider).AcquireAsync(
            "k", new DistributedLockOptions { AutoRenew = false });

        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(
            () => new DistributedLock(provider).AcquireAsync("k", new DistributedLockOptions
            {
                WaitTimeout = TimeSpan.FromMilliseconds(100),
                RetryInterval = TimeSpan.FromSeconds(10),
                AutoRenew = false,
            }));
    }

    [Fact]
    public async Task ALeaseShorterThanTheRetryInterval_IsStillAccepted_BecauseAnUncontendedAcquireNeverWaits()
    {
        // Also not a misconfiguration: the first attempt succeeds without consulting RetryInterval at all.
        var sut = NewLock();

        await using var held = await sut.AcquireAsync("k", new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(50),
            RetryInterval = TimeSpan.FromSeconds(1),
            AutoRenew = false,
        });

        Assert.True(held.IsHeld);
    }
}
