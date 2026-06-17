using Moongazing.OrionLock.Fairness;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Demo;

/// <summary>
/// Models two independent "nodes" (processes) sharing one backend store.
/// <para>
/// OrionLock's reentrancy is scoped to a single <see cref="DistributedLock"/> instance: the same
/// instance re-acquiring a key it holds gets a counted nested handle without contending. To
/// demonstrate REAL cross-process contention against the in-memory backend, the two
/// <see cref="DistributedLock"/> instances below share one <see cref="InMemoryLockProvider"/> (the
/// shared "backend store"), so a lock held by node A genuinely blocks node B.
/// </para>
/// </summary>
internal sealed class LockNodes
{
    private LockNodes(IDistributedLock nodeA, IDistributedLock nodeB)
    {
        NodeA = nodeA;
        NodeB = nodeB;
    }

    /// <summary>First node. Holds locks that node B must contend for.</summary>
    public IDistributedLock NodeA { get; }

    /// <summary>Second node. Independent reentrancy scope; shares node A's backend store.</summary>
    public IDistributedLock NodeB { get; }

    /// <summary>Build two nodes over a single shared in-memory backend store.</summary>
    public static LockNodes Create()
    {
        var sharedBackend = new InMemoryLockProvider();
        return new LockNodes(
            new DistributedLock(sharedBackend),
            new DistributedLock(sharedBackend));
    }

    /// <summary>
    /// Build two nodes over a shared backend where node B is wired with an in-process FIFO waiter
    /// coordinator. Used by the fairness demo: node A holds the key (so node B genuinely contends),
    /// and all of node B's waiters share the one coordinator so they queue in arrival order.
    /// </summary>
    public static LockNodes CreateWithFifoNodeB()
    {
        var sharedBackend = new InMemoryLockProvider();
        var fifoCoordinator = new InProcessFifoWaiterCoordinator();
        return new LockNodes(
            new DistributedLock(sharedBackend),
            new DistributedLock(sharedBackend, fifoCoordinator));
    }
}
