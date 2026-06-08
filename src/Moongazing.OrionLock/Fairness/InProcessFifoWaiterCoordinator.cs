namespace Moongazing.OrionLock.Fairness;

using System.Collections.Concurrent;

/// <summary>
/// In-process FIFO waiter coordination backed by a per-key linked list of
/// <see cref="TaskCompletionSource"/>. Useful for single-process deployments where a
/// thundering herd of <c>AcquireAsync</c> callers needs deterministic ordering without
/// reaching for Redis or ZooKeeper.
/// </summary>
/// <remarks>
/// Distributed (cross-process) FIFO ordering is on the v0.3.x roadmap. The contract is
/// stable; backends will plug in via <see cref="IFifoWaiterCoordinator"/> without source-breaking
/// changes.
/// </remarks>
public sealed class InProcessFifoWaiterCoordinator : IFifoWaiterCoordinator
{
    private readonly ConcurrentDictionary<string, Queue<WaiterTicket>> queues = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <inheritdoc />
    public Task<IFifoWaiterTicket> EnterAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cancellationToken.ThrowIfCancellationRequested();

        var ticket = new WaiterTicket(key);
        bool head;

        lock (gate)
        {
            var queue = queues.GetOrAdd(key, _ => new Queue<WaiterTicket>());
            head = queue.Count == 0;
            queue.Enqueue(ticket);
        }

        if (head)
        {
            // First in line - complete immediately so the caller can attempt acquire.
            ticket.Tcs.TrySetResult();
        }
        else
        {
            // Wire cancellation to the in-flight TCS so the wait can be cancelled cleanly.
            var registration = cancellationToken.Register(static state =>
            {
                var t = (WaiterTicket)state!;
                t.Tcs.TrySetCanceled(t.CancellationToken);
            }, ticket);
            ticket.Registration = registration;
        }

        return WrapAsync(ticket);
    }

    private static async Task<IFifoWaiterTicket> WrapAsync(WaiterTicket ticket)
    {
        await ticket.Tcs.Task.ConfigureAwait(false);
        return ticket;
    }

    /// <inheritdoc />
    public Task LeaveAsync(IFifoWaiterTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (ticket is not WaiterTicket waiter)
        {
            throw new ArgumentException(
                $"Ticket was not issued by this queue. Got '{ticket.GetType().FullName}'.",
                nameof(ticket));
        }

        WaiterTicket? next = null;
        lock (gate)
        {
            if (!queues.TryGetValue(waiter.Key, out var queue))
            {
                return Task.CompletedTask;
            }

            if (queue.Count > 0 && ReferenceEquals(queue.Peek(), waiter))
            {
                queue.Dequeue();
                while (queue.Count > 0)
                {
                    var candidate = queue.Peek();
                    if (candidate.Tcs.Task.IsCanceled)
                    {
                        queue.Dequeue();
                        continue;
                    }
                    next = candidate;
                    break;
                }
            }
            else
            {
                // Not the head - mark cancelled so the dequeue path skips them later.
                waiter.Tcs.TrySetCanceled(CancellationToken.None);
            }

            if (queue.Count == 0)
            {
                queues.TryRemove(waiter.Key, out _);
            }
        }

        next?.Tcs.TrySetResult();
        waiter.Registration.Dispose();
        return Task.CompletedTask;
    }

    private sealed class WaiterTicket : IFifoWaiterTicket
    {
        public WaiterTicket(string key) { Key = key; }
        public string Key { get; }
        public TaskCompletionSource Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken CancellationToken { get; init; }
        public CancellationTokenRegistration Registration { get; set; }
    }
}
