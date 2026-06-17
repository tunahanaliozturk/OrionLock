using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Demo;

/// <summary>
/// OrionLock exposes two observability surfaces:
/// <list type="bullet">
/// <item>A consumer-supplied <see cref="ILockEventObserver"/> - synchronous audit hooks for
/// acquire / timeout / release without coupling audit logic to the lock path.</item>
/// <item>An OpenTelemetry <see cref="Meter"/> named <c>Moongazing.OrionLock</c> emitting acquisition
/// and contention counters that any <see cref="MeterListener"/> (or the OTel SDK) can collect.</item>
/// </list>
/// Node A holds keys so node B's contended acquire genuinely times out and fires
/// <c>OnAcquireTimedOut</c>; the observer is wired into node B.
/// </summary>
internal static class ObservabilityDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Section("7. Observability (event observer + OpenTelemetry meter)");

        // Subscribe a MeterListener to the OrionLock instruments before any acquire happens.
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        var countersLock = new object();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OrionLockDiagnostics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            lock (countersLock)
            {
                counters.TryGetValue(instrument.Name, out var current);
                counters[instrument.Name] = current + measurement;
            }
        });
        listener.Start();

        var observer = new ConsoleLockEventObserver();

        // Shared backend store; the observer is wired into node B (the observed node).
        var sharedBackend = new InMemoryLockProvider();
        var nodeA = new DistributedLock(sharedBackend);
        var nodeB = new DistributedLock(sharedBackend, fifoCoordinator: null, eventObserver: observer);

        DemoConsole.Step("Node B acquires and releases 'metrics:key' (observer fires OnAcquired / OnReleased)...");
        await using (var handle = await nodeB.AcquireAsync("metrics:key"))
        {
            // held
        }

        DemoConsole.Step("Node A holds 'metrics:key'; node B's contended acquire times out (OnAcquireTimedOut)...");
        await using var blocker = await nodeA.AcquireAsync("metrics:key");
        try
        {
            await using var _ = await nodeB.AcquireAsync(
                "metrics:key",
                new DistributedLockOptions { WaitTimeout = TimeSpan.FromMilliseconds(200), RetryInterval = TimeSpan.FromMilliseconds(40) });
        }
        catch (LockAcquisitionTimeoutException)
        {
            // expected - the observer already reported the timeout.
        }

        // Flush any observable instruments.
        listener.RecordObservableInstruments();

        DemoConsole.Result($"Observer tally: acquired={observer.Acquired}, released={observer.Released}, timedOut={observer.TimedOut}");

        lock (countersLock)
        {
            DemoConsole.Result("OpenTelemetry counters captured from the Moongazing.OrionLock meter:");
            foreach (var (name, value) in counters.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                DemoConsole.Result($"    {name} = {value}");
            }
            if (counters.Count == 0)
            {
                DemoConsole.Result("    (no long-counter measurements recorded on this run)");
            }
        }
    }

    /// <summary>A trivial observer that counts lifecycle callbacks for the demo.</summary>
    private sealed class ConsoleLockEventObserver : ILockEventObserver
    {
        private long acquired;
        private long released;
        private long timedOut;

        public long Acquired => Interlocked.Read(ref acquired);
        public long Released => Interlocked.Read(ref released);
        public long TimedOut => Interlocked.Read(ref timedOut);

        public void OnAcquired(string key, double durationMs)
        {
            Interlocked.Increment(ref acquired);
            DemoConsole.Result($"  [observer] OnAcquired key='{key}' durationMs={durationMs:F1}");
        }

        public void OnAcquireTimedOut(string key, double waitMs)
        {
            Interlocked.Increment(ref timedOut);
            DemoConsole.Result($"  [observer] OnAcquireTimedOut key='{key}' waitMs={waitMs:F1}");
        }

        public void OnLeaseLost(string key)
            => DemoConsole.Result($"  [observer] OnLeaseLost key='{key}'");

        public void OnReleased(string key)
        {
            Interlocked.Increment(ref released);
            DemoConsole.Result($"  [observer] OnReleased key='{key}'");
        }
    }
}
