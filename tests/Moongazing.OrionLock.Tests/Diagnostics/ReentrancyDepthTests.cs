namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Testing;
using Xunit;

[CollectionDefinition(nameof(ReentrancyDepthTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class ReentrancyDepthTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(ReentrancyDepthTests))]
public sealed class ReentrancyDepthTests
{
    [Fact]
    public async Task Nested_acquire_increments_depth_and_dispose_returns_to_zero()
    {
        long current = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orion.lock.reentrancy.depth")
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

        var outer = await sut.AcquireAsync("k",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5), AutoRenew = false });
        // Nested acquire on the SAME instance / SAME key returns a reentrant handle and
        // should increment the depth gauge.
        Assert.Equal(0, Interlocked.Read(ref current));

        var nested = await sut.AcquireAsync("k",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5), AutoRenew = false });
        Assert.Equal(1, Interlocked.Read(ref current));

        await nested.DisposeAsync();
        Assert.Equal(0, Interlocked.Read(ref current));

        await outer.DisposeAsync();
        // Outer dispose does not change the gauge (it was never incremented; only the
        // nested re-entry was counted).
        Assert.Equal(0, Interlocked.Read(ref current));
    }
}
