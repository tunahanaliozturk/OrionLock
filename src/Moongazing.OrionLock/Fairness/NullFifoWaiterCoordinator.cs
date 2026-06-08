namespace Moongazing.OrionLock.Fairness;

/// <summary>
/// Default <see cref="IFifoWaiterCoordinator"/> registration. No coordination: every
/// <see cref="EnterAsync"/> completes immediately with a no-op ticket. Preserves the v0.3.1
/// polling-retry behaviour so consumers see no behavioural change unless they register an
/// alternate implementation.
/// </summary>
public sealed class NullFifoWaiterCoordinator : IFifoWaiterCoordinator
{
    /// <inheritdoc />
    public Task<IFifoWaiterTicket> EnterAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Task.FromResult<IFifoWaiterTicket>(new NullTicket(key));
    }

    /// <inheritdoc />
    public Task LeaveAsync(IFifoWaiterTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return Task.CompletedTask;
    }

    private sealed record NullTicket(string Key) : IFifoWaiterTicket;
}
