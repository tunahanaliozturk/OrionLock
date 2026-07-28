namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Testing;
using Xunit;

[CollectionDefinition(nameof(AcquireTimeoutCounterTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class AcquireTimeoutCounterTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(AcquireTimeoutCounterTests))]
public sealed class AcquireTimeoutCounterTests
{
    private static (long count, MeterListener listener) BuildListener(ref long counter)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orion.lock.acquire.timeout")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        return (0, listener);
    }

    [Fact]
    public void Direct_RecordAcquireTimeout_emits_a_single_increment()
    {
        long count = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orion.lock.acquire.timeout")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref count, val));
        listener.Start();

        OrionLockDiagnostics.RecordAcquireTimeout();

        Assert.Equal(1, Interlocked.Read(ref count));
    }

    [Fact]
    public void RecordAcquireTimeout_inherits_static_metric_labels()
    {
        // Same-process metrics labels carry through (mirrors v0.3.12 WithMetricsLabel
        // tests on the other counters).
        var previous = OrionLockDiagnostics.StaticTags;
        try
        {
            var services = new ServiceCollection();
            services.AddOrionLock().WithMetricsLabel("tenant", "acme");

            string? captured = null;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Moongazing.OrionLock"
                    && instrument.Name == "orion.lock.acquire.timeout")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var t in tags)
                {
                    if (t.Key == "tenant" && t.Value is string s) { captured = s; }
                }
            });
            listener.Start();

            OrionLockDiagnostics.RecordAcquireTimeout();

            Assert.Equal("acme", captured);
        }
        finally
        {
            var revert = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in previous)
            {
                if (p.Value is string s) { revert[p.Key] = s; }
            }
            OrionLockDiagnostics.SetStaticTags(revert);
        }
    }
}
