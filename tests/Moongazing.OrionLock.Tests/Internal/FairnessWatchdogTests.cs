namespace Moongazing.OrionLock.Tests.Internal;

using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;
using Moq;
using Xunit;

public sealed class FairnessWatchdogTests
{
    [Fact]
    public async Task Lost_token_fires_when_renew_failures_exceed_grace_period()
    {
        // Test the watchdog directly: the grace period is short, renew always throws,
        // and the clock advances so the deadline elapses.
        var provider = new Mock<IDistributedLockProvider>();
        provider.Setup(p => p.TryRenewAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("backend unreachable"));

        var now = new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);
        DateTime Clock() => now;

        await using var handle = new DistributedLockHandle(
            provider.Object,
            key: "k",
            ownerToken: "owner",
            new DistributedLockOptions
            {
                LeaseDuration = TimeSpan.FromMilliseconds(60),
                AutoRenew = true,
                RenewalFailureGracePeriod = TimeSpan.FromMilliseconds(40),
            },
            nowUtc: () => Clock());

        Assert.True(handle.IsHeld);

        // Advance the clock past the grace period and wait for the watchdog tick.
        now = now.AddMilliseconds(200);
        await Task.Delay(200);

        Assert.False(handle.IsHeld);
        Assert.True(handle.LostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Watchdog_clock_advances_per_renew_call_so_reset_window_logic_can_be_exercised()
    {
        // Each renew call advances the simulated clock by 10 ms. Successes keep
        // lastSuccessfulRenewalUtc up to date; once we flip to failure mode, the
        // deadline math should evaluate (now - lastSuccessful) against the grace
        // period using actual elapsed time.
        var provider = new Mock<IDistributedLockProvider>();
        var renewCalls = 0;
        var failNext = false;
        provider.Setup(p => p.TryRenewAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, TimeSpan, CancellationToken>((_, _, _, _) =>
            {
                renewCalls++;
                return failNext
                    ? Task.FromException<bool>(new InvalidOperationException("blip"))
                    : Task.FromResult(true);
            });

        var baseTime = new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);
        DateTime Clock() => baseTime.AddMilliseconds(renewCalls * 10);

        await using var handle = new DistributedLockHandle(
            provider.Object,
            key: "k",
            ownerToken: "owner",
            new DistributedLockOptions
            {
                LeaseDuration = TimeSpan.FromMilliseconds(60),
                AutoRenew = true,
                RenewalFailureGracePeriod = TimeSpan.FromMilliseconds(40),
            },
            nowUtc: Clock);

        // Wait long enough for several renewals to fire (PeriodicTimer ticks at
        // LeaseDuration/3 = 20 ms in real time). After each success, lastSuccessful
        // advances by ~10 ms of simulated time.
        await Task.Delay(150);
        Assert.True(handle.IsHeld);

        // Flip to failures: the FIRST failure ticks the clock by another 10 ms - well
        // under the 40 ms grace from the most recent success. IsHeld must stay true.
        failNext = true;
        await Task.Delay(30);
        Assert.True(handle.IsHeld);
    }
}
