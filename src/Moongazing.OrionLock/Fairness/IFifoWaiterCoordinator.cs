namespace Moongazing.OrionLock.Fairness;

/// <summary>
/// Optional FIFO waiter coordination for blocking <c>AcquireAsync</c>. Concrete implementations
/// (in-process, Redis, ZooKeeper) ensure waiters acquire the lock in arrival order rather than
/// the polling-retry default where the first thread to land on a free slot wins.
/// </summary>
/// <remarks>
/// v0.3.2 ships the contract and the in-process implementation. The integration into
/// <c>DistributedLockOptions</c> and the <c>AcquireAsync</c> retry loop is staged for v0.3.3,
/// so consumers who register an implementation today see no behavioural change yet. The
/// staged rollout gives backends time to ship distributed (cross-process) queueing without
/// changing the interface afterwards.
/// </remarks>
public interface IFifoWaiterCoordinator
{
    /// <summary>
    /// Enter the FIFO queue for <paramref name="key"/>. The returned task completes when the
    /// caller is at the head of the queue and may attempt to acquire the lock. The caller
    /// MUST call <see cref="LeaveAsync"/> after the attempt completes (success or failure),
    /// otherwise downstream waiters block forever.
    /// </summary>
    /// <param name="key">Lock key identifying the queue.</param>
    /// <param name="cancellationToken">Cancellation token observed during the wait.</param>
    /// <returns>An opaque ticket identifying the caller's slot in the queue. Pass it to
    /// <see cref="LeaveAsync"/> when releasing.</returns>
    Task<IFifoWaiterTicket> EnterAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Leave the FIFO queue. Pops the next waiter (if any) so their <see cref="EnterAsync"/>
    /// task completes. Idempotent: calling Leave twice on the same ticket is a no-op.
    /// </summary>
    /// <param name="ticket">Ticket previously returned from <see cref="EnterAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LeaveAsync(IFifoWaiterTicket ticket, CancellationToken cancellationToken);
}

/// <summary>
/// Opaque handle returned from <see cref="IFifoWaiterCoordinator.EnterAsync"/>. Carries enough
/// information for the queue to identify the caller during <see cref="IFifoWaiterCoordinator.LeaveAsync"/>.
/// Implementations are free to use any concrete shape.
/// </summary>
public interface IFifoWaiterTicket
{
    /// <summary>The lock key this ticket belongs to.</summary>
    string Key { get; }
}
