namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Xunit;

[CollectionDefinition(nameof(HealthCheckLastCheckAtTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class HealthCheckLastCheckAtTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(HealthCheckLastCheckAtTests))]
public sealed class HealthCheckLastCheckAtTests
{
    [Fact]
    public void Gauge_reports_the_recorded_unix_seconds()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.health.last_check_at_unix_seconds")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) =>
        {
            long current;
            do { current = Interlocked.Read(ref observed); }
            while (val > current && Interlocked.CompareExchange(ref observed, val, current) != current);
        });
        listener.Start();

        var when = new DateTime(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);
        OrionLockDiagnostics.RecordHealthCheckCompleted(when);
        listener.RecordObservableInstruments();

        var expected = new DateTimeOffset(when).ToUnixTimeSeconds();
        Assert.True(Interlocked.Read(ref observed) >= expected);
    }
}
