namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Xunit;

[CollectionDefinition(nameof(ExpiredBeforeReleaseTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class ExpiredBeforeReleaseTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(ExpiredBeforeReleaseTests))]
public sealed class ExpiredBeforeReleaseTests
{
    [Fact]
    public void Direct_RecordLeaseExpiredBeforeRelease_emits_a_single_increment()
    {
        long count = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orionlock.lease.expired_before_release")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref count, val));
        listener.Start();

        typeof(OrionLockDiagnostics)
            .GetMethod("RecordLeaseExpiredBeforeRelease",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null);

        Assert.Equal(1, Interlocked.Read(ref count));
    }
}
