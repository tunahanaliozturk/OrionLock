using BenchmarkDotNet.Attributes;
using Moongazing.OrionLock.Fairness;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// Measures the uncontended fast path of <see cref="InProcessFifoWaiterCoordinator"/>: a single
/// caller enters the per-key FIFO queue, becomes the head immediately (no wait), then leaves. This
/// is the cost the fair-lock option adds to every blocking <c>AcquireAsync</c> when the queue is
/// empty, which is the common case under low contention. It exercises the real Enter/Leave contract
/// (queue allocation, head detection, queue-depth metric emission, ticket disposal) so the measured
/// figure is the floor that opting into FIFO ordering imposes before any actual contention exists.
/// </summary>
[MultiRuntimeConfig]
public class FifoCoordinatorBenchmarks
{
    private readonly InProcessFifoWaiterCoordinator coordinator = new();
    private const string Key = "bench-fifo-key";

    /// <summary>Enter the empty queue (become head), then leave. The uncontended round trip.</summary>
    [Benchmark]
    public async Task EnterLeaveUncontended()
    {
        var ticket = await coordinator.EnterAsync(Key, CancellationToken.None).ConfigureAwait(false);
        await coordinator.LeaveAsync(ticket, CancellationToken.None).ConfigureAwait(false);
    }
}
