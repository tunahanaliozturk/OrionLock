namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;
using Xunit;

// The held_concurrent gauge is a process-wide instrument, so this collection must not run alongside
// anything else that takes a lease - the assertion here is an absolute zero, not a lower bound.
[CollectionDefinition(nameof(LeaseExpiryLossPathTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class LeaseExpiryLossPathTestsCollection { }
#pragma warning restore CA1711

/// <summary>
/// A lease that runs out with <c>AutoRenew = false</c> must go through the same surrender path the
/// watchdog uses for a confirmed loss. Tripping <c>LostToken</c> alone is not enough: it would leave
/// <c>orion.lock.leases.held_concurrent</c> incremented forever, never count the loss, never tell the
/// observer, and leave the internal held flag set so a later dispose reports a release for a lease that
/// was already gone.
/// </summary>
[Collection(nameof(LeaseExpiryLossPathTests))]
public sealed class LeaseExpiryLossPathTests
{
    private sealed class GrantingProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(true);

        public Task ReleaseAsync(string k, string o, CancellationToken c) => Task.CompletedTask;
    }

    private sealed class RecordingObserver : ILockEventObserver
    {
        private readonly List<string> events = [];

        public IReadOnlyList<string> Events
        {
            get { lock (events) { return [.. events]; } }
        }

        public void OnAcquired(string key, double durationMs) { }

        public void OnAcquireTimedOut(string key, double waitMs) { }

        public void OnLeaseLost(string key) { lock (events) { events.Add($"lost:{key}"); } }

        public void OnReleased(string key) { lock (events) { events.Add($"released:{key}"); } }
    }

    [Fact]
    public async Task Lease_expiry_without_autorenew_runs_the_full_loss_path()
    {
        long gauge = 0;
        long leasesLost = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != "Moongazing.OrionLock") return;
            if (instrument.Name is "orion.lock.leases.held_concurrent" or "orion.lock.lease.lost")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instr, val, _, _) =>
        {
            if (instr.Name == "orion.lock.leases.held_concurrent") Interlocked.Add(ref gauge, val);
            if (instr.Name == "orion.lock.lease.lost") Interlocked.Add(ref leasesLost, val);
        });
        listener.Start();

        var observer = new RecordingObserver();
        // Stands in for the increment DistributedLock performs when it takes a real backend lease; the
        // handle owns the matching decrement.
        OrionLockDiagnostics.IncrementLeasesHeld();
        Assert.Equal(1, Interlocked.Read(ref gauge));

        var handle = new DistributedLockHandle(
            new GrantingProvider(), "k", "owner-1",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(100), AutoRenew = false },
            observer);

        var lost = new TaskCompletionSource();
        handle.LostToken.Register(() => lost.TrySetResult());
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, Interlocked.Read(ref gauge));
        Assert.True(Interlocked.Read(ref leasesLost) >= 1);
        Assert.Equal(["lost:k"], observer.Events);
        Assert.False(handle.IsHeld);

        // Disposing afterwards must not double-decrement the gauge, and must not report a release for
        // a lease the expiry already gave up.
        await handle.DisposeAsync();

        Assert.Equal(0, Interlocked.Read(ref gauge));
        Assert.Equal(["lost:k"], observer.Events);
    }
}
