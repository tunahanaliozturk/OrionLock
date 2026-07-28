namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
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
    private const string InstrumentName = "orion.lock.fairness.queue_depth";

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

    [Fact]
    public async Task EnterAsync_excludes_cancelled_waiters_from_the_recorded_depth()
    {
        var samples = new List<int>();
        using var listener = StartListener(samples);

        var coordinator = new InProcessFifoWaiterCoordinator();

        // Head of the queue (depth 0).
        var first = await coordinator.EnterAsync("cancel-key", default);

        // Second joins behind the head (depth 1) but is cancelled while still waiting. Its ticket
        // is only marked cancelled - it lingers in the queue until the head's LeaveAsync prunes it.
        using var cts = new CancellationTokenSource();
        var secondTask = coordinator.EnterAsync("cancel-key", cts.Token);

        // Third joins behind head + second (depth 2).
        var thirdTask = coordinator.EnterAsync("cancel-key", default);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);

        int before;
        lock (samples) { before = samples.Count; }

        // Fourth enter: the live waiters ahead are head + third = 2. The cancelled-but-lingering
        // second must NOT be counted, so the recorded depth is 2, never 3.
        var fourthTask = coordinator.EnterAsync("cancel-key", default);

        lock (samples)
        {
            var fresh = samples.Skip(before).ToList();
            Assert.Contains(2, fresh);
            Assert.DoesNotContain(3, fresh);
        }

        // Drain in queue order: head leaves and prunes the cancelled second, handing the slot to
        // third; third must leave before fourth can become the head, so await each task only after
        // the waiter ahead of it has released.
        await coordinator.LeaveAsync(first, default);
        var third = await thirdTask;
        await coordinator.LeaveAsync(third, default);
        var fourth = await fourthTask;
        await coordinator.LeaveAsync(fourth, default);
    }
}
