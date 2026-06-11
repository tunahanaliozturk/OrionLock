namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Testing;
using Xunit;

[CollectionDefinition(nameof(LeasesHeldConcurrentTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class LeasesHeldConcurrentTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(LeasesHeldConcurrentTests))]
public sealed class LeasesHeldConcurrentTests
{
    private static (long current, MeterListener listener) TrackHeld()
    {
        long current = 0;
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.leases.held_concurrent")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref current, val));
        listener.Start();
        return (current, listener);
    }

    [Fact]
    public async Task Acquire_increments_decrement_on_dispose_returns_to_zero()
    {
        long current = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.leases.held_concurrent")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref current, val));
        listener.Start();

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var sut = sp.GetRequiredService<IDistributedLock>();

        var handle = await sut.AcquireAsync("k", new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5), AutoRenew = false });
        Assert.Equal(1, Interlocked.Read(ref current));

        await handle.DisposeAsync();
        Assert.Equal(0, Interlocked.Read(ref current));
    }

    [Fact]
    public async Task Dispose_called_twice_decrements_only_once()
    {
        long current = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.leases.held_concurrent")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref current, val));
        listener.Start();

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var sut = sp.GetRequiredService<IDistributedLock>();

        var handle = await sut.AcquireAsync("k2", new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5), AutoRenew = false });
        await handle.DisposeAsync();
        await handle.DisposeAsync();
        await handle.DisposeAsync();

        Assert.Equal(0, Interlocked.Read(ref current));
    }
}
