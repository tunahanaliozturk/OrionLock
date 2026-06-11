namespace Moongazing.OrionLock.Tests.Internal;

using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;
using Moq;
using Xunit;

[CollectionDefinition(nameof(FairnessWatchdogTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class FairnessWatchdogTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(FairnessWatchdogTests))]
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
    public async Task GraceExhausted_counter_increments_in_addition_to_LeasesLost_on_fairness_release()
    {
        // Listen on the orionlock Meter so the v0.3.11 grace_period_exhausted counter
        // surfaces. Both counters MUST increment by 1 on a fairness-watchdog release.
        var leasesLost = 0L;
        var graceExhausted = 0L;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != "Moongazing.OrionLock") return;
            if (instrument.Name == "orionlock.lease.lost") l.EnableMeasurementEvents(instrument);
            if (instrument.Name == "orionlock.lease.grace_period_exhausted") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instr, val, tags, state) =>
        {
            if (instr.Name == "orionlock.lease.lost") Interlocked.Add(ref leasesLost, val);
            if (instr.Name == "orionlock.lease.grace_period_exhausted") Interlocked.Add(ref graceExhausted, val);
        });
        listener.Start();

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

        now = now.AddMilliseconds(200);
        await Task.Delay(200);

        Assert.False(handle.IsHeld);
        // Use >= 1 rather than == 1 because the MeterListener is process-wide and other
        // tests in the same process can also emit on these counters. The collection-level
        // DisableParallelization keeps unrelated tests from running concurrently, but the
        // counters are static so any prior test in the same run is reflected here.
        Assert.True(Interlocked.Read(ref leasesLost) >= 1);
        Assert.True(Interlocked.Read(ref graceExhausted) >= 1);
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
