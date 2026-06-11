namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Xunit;

[CollectionDefinition(nameof(ConsecutiveRenewalFailuresTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class ConsecutiveRenewalFailuresTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(ConsecutiveRenewalFailuresTests))]
public sealed class ConsecutiveRenewalFailuresTests
{
    [Fact]
    public void RecordConsecutiveRenewalFailures_emits_for_positive_count()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.lease.renewal_failures_consecutive")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        OrionLockDiagnostics.RecordConsecutiveRenewalFailures(7);

        lock (samples) { Assert.Contains(7, samples); }
    }

    [Fact]
    public void RecordConsecutiveRenewalFailures_ignores_zero_and_negative_input()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.lease.renewal_failures_consecutive")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        OrionLockDiagnostics.RecordConsecutiveRenewalFailures(0);
        OrionLockDiagnostics.RecordConsecutiveRenewalFailures(-3);

        lock (samples)
        {
            Assert.DoesNotContain(0, samples);
            Assert.DoesNotContain(-3, samples);
        }
    }
}
