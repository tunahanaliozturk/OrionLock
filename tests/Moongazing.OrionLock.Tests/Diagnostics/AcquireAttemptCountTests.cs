namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Xunit;

[CollectionDefinition(nameof(AcquireAttemptCountTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class AcquireAttemptCountTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(AcquireAttemptCountTests))]
public sealed class AcquireAttemptCountTests
{
    [Fact]
    public void RecordAcquireAttemptCount_emits_for_positive_count()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.acquire.attempt_count")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        typeof(OrionLockDiagnostics)
            .GetMethod("RecordAcquireAttemptCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { 12 });

        lock (samples) { Assert.Contains(12, samples); }
    }

    [Fact]
    public void RecordAcquireAttemptCount_ignores_zero_and_negative()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.acquire.attempt_count")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        var method = typeof(OrionLockDiagnostics)
            .GetMethod("RecordAcquireAttemptCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        method.Invoke(null, new object[] { 0 });
        method.Invoke(null, new object[] { -5 });

        lock (samples)
        {
            Assert.DoesNotContain(0, samples);
            Assert.DoesNotContain(-5, samples);
        }
    }
}
