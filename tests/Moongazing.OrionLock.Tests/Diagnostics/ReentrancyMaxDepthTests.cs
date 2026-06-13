namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;
using Xunit;

[CollectionDefinition(nameof(ReentrancyMaxDepthTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class ReentrancyMaxDepthTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(ReentrancyMaxDepthTests))]
public sealed class ReentrancyMaxDepthTests
{
    [Fact]
    public async Task Nested_reacquire_records_the_peak_depth_on_final_dispose()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.reentrancy.max_depth")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var sut = sp.GetRequiredService<IDistributedLock>();
        var opts = new DistributedLockOptions { LeaseDuration = System.TimeSpan.FromSeconds(5), AutoRenew = false };

        // outer -> nested -> nested (peak depth 3), then unwind.
        var h1 = await sut.AcquireAsync("nest", opts);
        var h2 = await sut.AcquireAsync("nest", opts);
        var h3 = await sut.AcquireAsync("nest", opts);
        await h3.DisposeAsync();
        await h2.DisposeAsync();
        // No sample yet - still held by the outer handle.
        lock (samples) { Assert.Empty(samples); }
        await h1.DisposeAsync();

        lock (samples) { Assert.Contains(3, samples); }
    }

    [Fact]
    public void RecordReentrancyMaxDepth_ignores_zero_and_negative()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.reentrancy.max_depth")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        typeof(Moongazing.OrionLock.Diagnostics.OrionLockDiagnostics)
            .GetMethod("RecordReentrancyMaxDepth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { 0 });

        lock (samples) { Assert.DoesNotContain(0, samples); }
    }
}
