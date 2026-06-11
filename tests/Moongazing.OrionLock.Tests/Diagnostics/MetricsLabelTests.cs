namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Diagnostics;
using Xunit;

[CollectionDefinition(nameof(MetricsLabelTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class MetricsLabelTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(MetricsLabelTests))]
public sealed class MetricsLabelTests
{
    [Fact]
    public void WithMetricsLabel_stamps_tag_on_subsequent_acquisition_counter()
    {
        // Capture the current snapshot first so other tests' tags do not leak in.
        var previous = OrionLockDiagnostics.StaticTags;
        try
        {
            var services = new ServiceCollection();
            services.AddOrionLock().WithMetricsLabel("tenant", "acme");

            string? capturedTagKey = null;
            string? capturedTagValue = null;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Moongazing.OrionLock"
                    && instrument.Name == "orionlock.acquisitions")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var t in tags)
                {
                    if (t.Key == "tenant" && t.Value is string s)
                    {
                        capturedTagKey = t.Key;
                        capturedTagValue = s;
                    }
                }
            });
            listener.Start();

            OrionLockDiagnostics.RecordAcquisition();

            Assert.Equal("tenant", capturedTagKey);
            Assert.Equal("acme", capturedTagValue);
        }
        finally
        {
            OrionLockDiagnostics.SetStaticTags(SnapshotToDict(previous));
        }
    }

    [Fact]
    public void WithMetricsLabels_bulk_merges_and_later_keys_override()
    {
        var previous = OrionLockDiagnostics.StaticTags;
        try
        {
            var services = new ServiceCollection();
            services.AddOrionLock()
                .WithMetricsLabel("tenant", "stale")
                .WithMetricsLabels(new Dictionary<string, string>
                {
                    ["tenant"] = "fresh",
                    ["region"] = "eu-west-1",
                });

            var tags = SnapshotToDict(OrionLockDiagnostics.StaticTags);
            Assert.Equal("fresh", tags["tenant"]);
            Assert.Equal("eu-west-1", tags["region"]);
        }
        finally
        {
            OrionLockDiagnostics.SetStaticTags(SnapshotToDict(previous));
        }
    }

    [Fact]
    public void WithMetricsLabel_rejects_null_or_empty_arguments()
    {
        var services = new ServiceCollection();
        var builder = services.AddOrionLock();
        Assert.Throws<ArgumentNullException>(() => builder.WithMetricsLabel("k", null!));
        Assert.Throws<ArgumentException>(() => builder.WithMetricsLabel(string.Empty, "v"));
    }

    [Fact]
    public void When_no_label_is_set_counters_still_emit_untagged()
    {
        var previous = OrionLockDiagnostics.StaticTags;
        try
        {
            OrionLockDiagnostics.SetStaticTags(new Dictionary<string, string>());
            var observed = 0;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Moongazing.OrionLock"
                    && instrument.Name == "orionlock.acquisitions")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref observed, (int)val));
            listener.Start();

            OrionLockDiagnostics.RecordAcquisition();
            Assert.Equal(1, observed);
        }
        finally
        {
            OrionLockDiagnostics.SetStaticTags(SnapshotToDict(previous));
        }
    }

    private static Dictionary<string, string> SnapshotToDict(KeyValuePair<string, object?>[] snapshot)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in snapshot)
        {
            if (p.Value is string s)
            {
                d[p.Key] = s;
            }
        }
        return d;
    }
}
