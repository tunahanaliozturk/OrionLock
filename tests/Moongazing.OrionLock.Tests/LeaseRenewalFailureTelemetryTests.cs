using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Tests;

public class LeaseRenewalFailureTelemetryTests
{
    private sealed class ThrowingProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => throw new InvalidOperationException("simulated backend blip");

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class HealthyProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task TryRenewAsync_throws_records_failure_counter_and_rethrows()
    {
        var samples = new ConcurrentBag<(long Value, string Backend)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OrionLockDiagnostics.MeterName
                    && instrument.Name == "orionlock.lease_renewal.failures")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? backend = null;
            foreach (var tag in tags)
            {
                if (tag.Key == OrionLockDiagnostics.BackendTagName)
                {
                    backend = tag.Value as string;
                }
            }
            samples.Add((value, backend ?? "<missing>"));
        });
        listener.Start();

        var measured = new MeasuringLockProvider(new ThrowingProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            measured.TryRenewAsync("k", "owner", TimeSpan.FromSeconds(10), CancellationToken.None));

        listener.Dispose();

        Assert.Single(samples);
        var (value, backend) = samples.First();
        Assert.Equal(1, value);
        Assert.False(string.IsNullOrEmpty(backend), "backend tag should be present on the failure counter");
    }

    [Fact]
    public async Task TryRenewAsync_returns_true_does_not_record_failure()
    {
        var samples = new ConcurrentBag<long>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OrionLockDiagnostics.MeterName
                    && instrument.Name == "orionlock.lease_renewal.failures")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => samples.Add(value));
        listener.Start();

        var measured = new MeasuringLockProvider(new HealthyProvider());

        var ok = await measured.TryRenewAsync("k", "owner", TimeSpan.FromSeconds(10), CancellationToken.None);

        listener.Dispose();

        Assert.True(ok);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task TryRenewAsync_OperationCanceledException_does_not_record_failure()
    {
        var samples = new ConcurrentBag<long>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == OrionLockDiagnostics.MeterName
                    && instrument.Name == "orionlock.lease_renewal.failures")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => samples.Add(value));
        listener.Start();

        var measured = new MeasuringLockProvider(new CancelingProvider());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            measured.TryRenewAsync("k", "owner", TimeSpan.FromSeconds(10), cts.Token));

        listener.Dispose();

        Assert.Empty(samples);
    }

    private sealed class CancelingProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
