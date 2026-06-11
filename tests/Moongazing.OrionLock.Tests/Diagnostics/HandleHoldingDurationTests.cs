namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Testing;
using Xunit;

[CollectionDefinition(nameof(HandleHoldingDurationTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class HandleHoldingDurationTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(HandleHoldingDurationTests))]
public sealed class HandleHoldingDurationTests
{
    [Fact]
    public async Task DisposeAsync_records_one_holding_duration_sample()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.handle.holding_duration")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var sut = sp.GetRequiredService<IDistributedLock>();

        var handle = await sut.AcquireAsync("k",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5), AutoRenew = false });
        await Task.Delay(20);
        await handle.DisposeAsync();

        lock (samples)
        {
            Assert.Single(samples);
            Assert.True(samples[0] >= 15, $"Expected >= 15 ms, got {samples[0]}");
        }
    }

    [Fact]
    public async Task DisposeAsync_called_twice_records_only_once()
    {
        var samples = new System.Collections.Generic.List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.handle.holding_duration")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var sut = sp.GetRequiredService<IDistributedLock>();

        var handle = await sut.AcquireAsync("k2",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5), AutoRenew = false });
        await handle.DisposeAsync();
        await handle.DisposeAsync();
        await handle.DisposeAsync();

        lock (samples) Assert.Single(samples);
    }
}
