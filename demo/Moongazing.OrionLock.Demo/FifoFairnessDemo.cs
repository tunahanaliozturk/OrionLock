namespace Moongazing.OrionLock.Demo;

/// <summary>
/// With <c>DistributedLockOptions.UseFifoWaiterCoordinator</c> enabled and an in-process FIFO
/// coordinator wired into the node, blocking <c>AcquireAsync</c> callers acquire the lock in arrival
/// order rather than racing on the polling-retry loop.
/// <para>
/// Node A holds the key (so the waiters genuinely contend rather than collapsing into reentrant
/// handles), while three waiters on node B share one FIFO coordinator. They enqueue in a known order
/// and drain in that order once node A releases.
/// </para>
/// </summary>
internal static class FifoFairnessDemo
{
    private static readonly string[] ExpectedOrder = { "W1", "W2", "W3" };

    public static async Task RunAsync()
    {
        DemoConsole.Section("5. FIFO fairness (arrival-order acquire)");

        var nodes = LockNodes.CreateWithFifoNodeB();

        const string key = "report:nightly";
        var fifoOptions = new DistributedLockOptions
        {
            UseFifoWaiterCoordinator = true,
            WaitTimeout = TimeSpan.FromSeconds(10),
            RetryInterval = TimeSpan.FromMilliseconds(20),
        };

        // Node A holds the key so all of node B's waiters must queue through the coordinator.
        DemoConsole.Step($"Node A holds '{key}'. Three node-B waiters enqueue in order: W1, W2, W3.");
        var primary = await nodes.NodeA.AcquireAsync(key);

        var completionOrder = new List<string>();
        var orderLock = new object();

        async Task Waiter(string name, int enqueueDelayMs)
        {
            // Stagger the EnterAsync calls so arrival order is deterministic (W1 then W2 then W3).
            await Task.Delay(enqueueDelayMs);
            await using var handle = await nodes.NodeB.AcquireAsync(key, fifoOptions);
            lock (orderLock)
            {
                completionOrder.Add(name);
                DemoConsole.Result($"{name} acquired '{handle.Key}' (position {completionOrder.Count}).");
            }

            // Hold briefly so the next waiter in line gets its turn cleanly.
            await Task.Delay(40);
        }

        var w1 = Waiter("W1", 0);
        var w2 = Waiter("W2", 30);
        var w3 = Waiter("W3", 60);

        // Let all three reach the queue, then release node A so the FIFO line drains.
        await Task.Delay(150);
        DemoConsole.Step("Node A releases - the FIFO queue drains in arrival order.");
        await primary.DisposeAsync();

        await Task.WhenAll(w1, w2, w3);

        var ordered = string.Join(" -> ", completionOrder);
        DemoConsole.Result($"Completion order: {ordered}");
        DemoConsole.Result(completionOrder.SequenceEqual(ExpectedOrder)
            ? "Waiters acquired in strict arrival order (FIFO fairness held)."
            : "Order differed from arrival (timing jitter); FIFO targets arrival order under contention.");
    }
}
