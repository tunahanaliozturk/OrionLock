namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Providers;
using Xunit;

[CollectionDefinition(nameof(AcquireCancelledCounterTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class AcquireCancelledCounterTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(AcquireCancelledCounterTests))]
public sealed class AcquireCancelledCounterTests
{
    private const string InstrumentName = "orionlock.acquire.cancelled";

    private static MeterListener StartListener(System.Action<long> add)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock" && instrument.Name == InstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => add(val));
        listener.Start();
        return listener;
    }

    // A backend that never grants the lock, so AcquireAsync stays in its contended retry loop
    // until the caller's token trips the cancel path - deterministic, no reentrancy short-circuit.
    private sealed class AlwaysBusyProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, System.TimeSpan leaseDuration, System.Threading.CancellationToken cancellationToken)
            => Task.FromResult(false);
        public Task<bool> TryRenewAsync(string key, string ownerToken, System.TimeSpan leaseDuration, System.Threading.CancellationToken cancellationToken)
            => Task.FromResult(false);
        public Task ReleaseAsync(string key, string ownerToken, System.Threading.CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task A_cancelled_acquire_increments_the_counter_and_propagates()
    {
        long total = 0;
        using var listener = StartListener(v => System.Threading.Interlocked.Add(ref total, v));

        var sut = new DistributedLock(new AlwaysBusyProvider());
        var opts = new DistributedLockOptions
        {
            LeaseDuration = System.TimeSpan.FromSeconds(30),
            AutoRenew = false,
            RetryInterval = System.TimeSpan.FromMilliseconds(50),
            WaitTimeout = System.TimeSpan.FromSeconds(30),
        };

        using var cts = new System.Threading.CancellationTokenSource();
        await cts.CancelAsync();

        // The lock is never granted, so the loop reaches the retry delay where the cancelled
        // token trips: it must throw (cancellation propagates) AND count the cancellation.
        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
            () => sut.AcquireAsync("cancel-key", opts, cts.Token));

        Assert.True(System.Threading.Interlocked.Read(ref total) >= 1);
    }

    [Fact]
    public void RecordAcquireCancelled_increments_the_counter()
    {
        long total = 0;
        using var listener = StartListener(v => System.Threading.Interlocked.Add(ref total, v));

        typeof(Moongazing.OrionLock.Diagnostics.OrionLockDiagnostics)
            .GetMethod("RecordAcquireCancelled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null);

        Assert.Equal(1L, System.Threading.Interlocked.Read(ref total));
    }
}
