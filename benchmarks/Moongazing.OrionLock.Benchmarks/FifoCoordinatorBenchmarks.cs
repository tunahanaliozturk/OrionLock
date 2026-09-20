using BenchmarkDotNet.Attributes;
using Moongazing.OrionLock.Fairness;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// Measures <see cref="InProcessFifoWaiterCoordinator"/> across a range of queue depths: one caller
/// takes the head of the per-key FIFO queue, <see cref="QueueDepth"/> more line up behind it, then
/// the whole queue drains in arrival order.
/// </summary>
/// <remarks>
/// <para>
/// <c>QueueDepth = 0</c> is the uncontended round trip this class used to measure on its own: enter
/// an empty queue, become the head immediately, leave. That is the cost the opt-in fair-lock option
/// adds to every blocking <c>AcquireAsync</c> under low contention, and it stays the baseline row.
/// </para>
/// <para>
/// The larger depths exist because the empty queue structurally cannot show what the coordinator
/// does per enter: it counts the live waiters ahead of the newcomer with a LINQ scan over the whole
/// queue, under the coordinator's single process-wide lock. That scan is O(N) in the current queue
/// depth, so building a queue of N does O(N^2) scanning in total - and because the lock is global
/// rather than per key, every key in the process serializes behind it.
/// </para>
/// <para>
/// READ THE MEAN AS A BATCH, NOT AS A PER-WAITER COST. One invocation performs all
/// <c>QueueDepth + 1</c> enter/leave pairs, and there is no <c>OperationsPerInvoke</c> to divide by
/// (it must be a compile-time constant, which a <c>[Params]</c> value cannot be). So the reported
/// mean and allocations are the cost of building AND draining the whole queue. Dividing by
/// <c>QueueDepth + 1</c> is a derivation the reader has to do deliberately - it is not what the
/// table says.
/// </para>
/// </remarks>
[MultiRuntimeConfig]
public class FifoCoordinatorBenchmarks
{
    private readonly InProcessFifoWaiterCoordinator coordinator = new();
    private const string Key = "bench-fifo-key";

    /// <summary>How many waiters queue up behind the measured head before the queue drains.</summary>
    [Params(0, 8, 64, 256)]
    public int QueueDepth { get; set; }

    /// <summary>
    /// Enter as the head, line <see cref="QueueDepth"/> waiters up behind, then drain the queue in
    /// order. The waiters are enqueued synchronously (<c>EnterAsync</c> does its queue work before
    /// the first await), so the queue really is <c>QueueDepth + 1</c> deep before the head leaves
    /// and the handoff chain is deterministic rather than a race with the thread pool. One
    /// invocation is the WHOLE build-and-drain, so the reported mean and allocations cover
    /// <c>QueueDepth + 1</c> enter/leave pairs, not one.
    /// </summary>
    [Benchmark]
    public async Task EnterLeaveWithQueueDepth()
    {
        var head = await coordinator.EnterAsync(Key, CancellationToken.None).ConfigureAwait(false);

        var behind = new Task[QueueDepth];
        for (var i = 0; i < QueueDepth; i++)
        {
            behind[i] = EnterThenLeaveAsync();
        }

        await coordinator.LeaveAsync(head, CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(behind).ConfigureAwait(false);
    }

    private async Task EnterThenLeaveAsync()
    {
        var ticket = await coordinator.EnterAsync(Key, CancellationToken.None).ConfigureAwait(false);
        await coordinator.LeaveAsync(ticket, CancellationToken.None).ConfigureAwait(false);
    }
}
