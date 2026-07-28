namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Xunit;

[CollectionDefinition(nameof(FifoCoordinatorEnterDurationTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class FifoCoordinatorEnterDurationTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(FifoCoordinatorEnterDurationTests))]
public sealed class FifoCoordinatorEnterDurationTests
{
    [Fact]
    public void Direct_RecordFifoCoordinatorEnter_emits_a_sample()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orion.lock.fairness.coordinator_enter_duration")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        OrionLockDiagnostics.RecordFifoCoordinatorEnter(123.4);

        lock (samples) { Assert.Contains(123.4, samples); }
    }
}
