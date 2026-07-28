namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;
using Xunit;

[CollectionDefinition(nameof(RenewalsPerHoldTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class RenewalsPerHoldTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(RenewalsPerHoldTests))]
public sealed class RenewalsPerHoldTests
{
    private const string InstrumentName = "orion.lock.handle.renewals_per_hold";

    private static MeterListener StartListener(System.Collections.Generic.List<int> samples)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock" && instrument.Name == InstrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();
        return listener;
    }

    [Fact]
    public async Task An_auto_renew_off_hold_records_zero_renewals_on_dispose()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = StartListener(samples);

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var sut = sp.GetRequiredService<IDistributedLock>();
        var opts = new DistributedLockOptions { LeaseDuration = System.TimeSpan.FromSeconds(5), AutoRenew = false };

        var handle = await sut.AcquireAsync("renew-zero", opts);
        await handle.DisposeAsync();

        // The watchdog never ran, so the hold cost zero renewals - and zero IS recorded.
        lock (samples) { Assert.Contains(0, samples); }
    }

    [Fact]
    public void RecordRenewalsPerHold_emits_the_count()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = StartListener(samples);

        Invoke(7);

        lock (samples) { Assert.Contains(7, samples); }
    }

    [Fact]
    public void RecordRenewalsPerHold_clamps_negative_to_zero()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = StartListener(samples);

        Invoke(-3);

        lock (samples)
        {
            Assert.Contains(0, samples);
            Assert.DoesNotContain(-3, samples);
        }
    }

    private static void Invoke(int renewals) =>
        typeof(Moongazing.OrionLock.Diagnostics.OrionLockDiagnostics)
            .GetMethod("RecordRenewalsPerHold", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { renewals });
}
