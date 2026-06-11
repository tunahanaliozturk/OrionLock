namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Testing;
using Xunit;

[CollectionDefinition(nameof(ContentionDurationTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class ContentionDurationTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(ContentionDurationTests))]
public sealed class ContentionDurationTests
{
    private static MeterListener BuildListener(System.Collections.Generic.List<double> samples)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.contention.duration")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();
        return listener;
    }

    [Fact]
    public async Task Uncontended_acquire_does_not_emit_a_contention_sample()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = BuildListener(samples);

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var sut = sp.GetRequiredService<IDistributedLock>();

        var h = await sut.AcquireAsync("k",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5), AutoRenew = false });
        await h.DisposeAsync();

        lock (samples) Assert.Empty(samples);
    }

    [Fact]
    public void Direct_RecordContentionDuration_emits_a_sample()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = BuildListener(samples);

        OrionLockDiagnostics.RecordContentionDuration(42.5);

        lock (samples)
        {
            Assert.Contains(42.5, samples);
        }
    }
}
