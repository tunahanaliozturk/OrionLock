namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Fairness;
using Xunit;

[CollectionDefinition(nameof(FifoQueueDepthTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class FifoQueueDepthTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(FifoQueueDepthTests))]
public sealed class FifoQueueDepthTests
{
    private const string InstrumentName = "orionlock.fairness.queue_depth";

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
    public void RecordFifoQueueDepth_emits_the_value_and_clamps_negatives()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = StartListener(samples);

        OrionLockDiagnostics.RecordFifoQueueDepth(3);
        OrionLockDiagnostics.RecordFifoQueueDepth(-1);

        lock (samples)
        {
            Assert.Contains(3, samples);
            Assert.Contains(0, samples);
            Assert.DoesNotContain(-1, samples);
        }
    }

    [Fact]
    public async Task EnterAsync_records_the_depth_each_candidate_joins_behind()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = StartListener(samples);

        var coordinator = new InProcessFifoWaiterCoordinator();

        // First entrant becomes the head (depth 0). Recorded synchronously at enter.
        var first = await coordinator.EnterAsync("depth-key", default);

        // Second entrant joins behind the still-queued first (depth 1). The returned task stays
        // pending until the first leaves, but the depth is recorded synchronously when EnterAsync
        // is called, so we can assert it before unblocking the queue.
        var secondTask = coordinator.EnterAsync("depth-key", default);

        lock (samples)
        {
            Assert.Contains(0, samples);
            Assert.Contains(1, samples);
        }

        await coordinator.LeaveAsync(first, default);
        var second = await secondTask;
        await coordinator.LeaveAsync(second, default);
    }
}
