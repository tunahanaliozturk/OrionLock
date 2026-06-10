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
    public async Task Successful_renewal_resets_the_grace_period_window()
    {
        // First few renewals succeed, then one fails - the grace period should be
        // measured from the LAST success, not from acquisition time.
        var provider = new Mock<IDistributedLockProvider>();
        var failNext = false;
        provider.Setup(p => p.TryRenewAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, TimeSpan, CancellationToken>((_, _, _, _) =>
                failNext
                    ? Task.FromException<bool>(new InvalidOperationException("blip"))
                    : Task.FromResult(true));

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

        // Let two renewals succeed.
        await Task.Delay(120);
        Assert.True(handle.IsHeld);

        // Now flip to failure mode and advance under the grace period - should NOT
        // declare lost yet because the last success was just now.
        failNext = true;
        await Task.Delay(50);
        Assert.True(handle.IsHeld);
    }
}
