using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class TelemetryInstrumentsTests
{
    [Fact]
    public async Task Acquire_EmitsAcquireLatencyHistogram_TaggedWithBackend()
    {
        var samples = new ConcurrentBag<(double Value, string Backend)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OrionLockDiagnostics.MeterName
                    && instrument.Name == "orionlock.acquire.latency")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string? backend = null;
            foreach (var t in tags)
            {
                if (t.Key == OrionLockDiagnostics.BackendTagName && t.Value is string s)
                {
                    backend = s;
                    break;
                }
            }
            samples.Add((value, backend ?? "<missing>"));
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var locker = sp.GetRequiredService<IDistributedLock>();

        await using (await locker.AcquireAsync("k-latency-test",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) }))
        {
            // critical section closes immediately
        }

        // Flush listener buffer to be safe before snapshot.
        listener.RecordObservableInstruments();

        Assert.NotEmpty(samples);
        Assert.Contains(samples, s => s.Backend == "inmemory");
        Assert.All(samples, s => Assert.True(s.Value >= 0));
    }

    [Fact]
    public async Task Renew_EmitsLeaseRenewalDurationHistogram_TaggedWithBackend()
    {
        var samples = new ConcurrentBag<(double Value, string Backend)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OrionLockDiagnostics.MeterName
                    && instrument.Name == "orionlock.lease_renewal.duration")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string? backend = null;
            foreach (var t in tags)
            {
                if (t.Key == OrionLockDiagnostics.BackendTagName && t.Value is string s)
                {
                    backend = s;
                    break;
                }
            }
            samples.Add((value, backend ?? "<missing>"));
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var locker = sp.GetRequiredService<IDistributedLock>();

        // Short lease so the watchdog renews quickly (interval is leaseDuration / 3 = ~33 ms).
        var handle = await locker.AcquireAsync("k-renew-test",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(100) });
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        finally
        {
            await handle.DisposeAsync();
        }

        Assert.NotEmpty(samples);
        Assert.Contains(samples, s => s.Backend == "inmemory");
    }
}
