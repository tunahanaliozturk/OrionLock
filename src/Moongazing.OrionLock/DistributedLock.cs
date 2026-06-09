using System.Diagnostics;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock;

/// <summary>
/// The default <see cref="IDistributedLock"/>. Composes a backend <see cref="IDistributedLockProvider"/>
/// with a blocking-acquire retry loop, same-process reentrancy, and lease-renewing handles.
/// </summary>
public sealed class DistributedLock : IDistributedLock
{
    private readonly IDistributedLockProvider provider;
    private readonly Fairness.IFifoWaiterCoordinator fifoCoordinator;
    private readonly ReentrancyRegistry reentrancy = new();

    /// <summary>Creates a lock over the given backend provider.</summary>
    public DistributedLock(IDistributedLockProvider provider)
        : this(provider, fifoCoordinator: null)
    {
    }

    /// <summary>
    /// Creates a lock over the given backend provider with an optional FIFO waiter
    /// coordinator (v0.3.3). When <paramref name="fifoCoordinator"/> is null, a
    /// <see cref="Fairness.NullFifoWaiterCoordinator"/> is used so v0.3.2 behaviour is
    /// preserved unless the consumer opts in via
    /// <see cref="DistributedLockOptions.UseFifoWaiterCoordinator"/>.
    /// </summary>
    public DistributedLock(IDistributedLockProvider provider, Fairness.IFifoWaiterCoordinator? fifoCoordinator)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
        this.fifoCoordinator = fifoCoordinator ?? new Fairness.NullFifoWaiterCoordinator();
    }

    /// <inheritdoc />
    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();

        var nested = reentrancy.TryEnter(key);
        if (nested is not null)
        {
            return nested;
        }

        var ownerToken = Guid.NewGuid().ToString("N");
        var acquired = await provider
            .TryAcquireAsync(key, ownerToken, options.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);

        if (!acquired)
        {
            return null;
        }

        var real = new DistributedLockHandle(provider, key, ownerToken, options);
        return reentrancy.Register(key, real);
    }

    /// <inheritdoc />
    public async Task<IDistributedLockHandle> AcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();

        using var activity = OrionLockDiagnostics.ActivitySource.StartActivity($"OrionLock.Acquire {key}");
        activity?.SetTag("orionlock.key", key);

        // v0.3.3: opt-in FIFO ordering. When enabled, the caller waits for its turn at the
        // head of the per-key queue BEFORE entering the polling-retry loop. LeaveAsync runs
        // in a finally so a thrown timeout / cancellation does not strand subsequent waiters.
        Fairness.IFifoWaiterTicket? ticket = null;
        if (options.UseFifoWaiterCoordinator)
        {
            ticket = await fifoCoordinator.EnterAsync(key, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                var handle = await TryAcquireAsync(key, options, cancellationToken).ConfigureAwait(false);
                if (handle is not null)
                {
                    activity?.SetTag("orionlock.outcome", "acquired");
                    OrionLockDiagnostics.Acquisitions.Add(1);
                    OrionLockDiagnostics.AcquireDuration.Record(deadline.Elapsed.TotalMilliseconds);
                    return handle;
                }

                OrionLockDiagnostics.Contentions.Add(1);

                if (deadline.Elapsed >= options.WaitTimeout)
                {
                    activity?.SetTag("orionlock.outcome", "timeout");
                    throw new LockAcquisitionTimeoutException(key, deadline.Elapsed);
                }

                await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ticket is not null)
            {
                await fifoCoordinator.LeaveAsync(ticket, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Acquires the lock using a lease of <paramref name="leaseDuration"/> and default wait/retry.</summary>
    public Task<IDistributedLockHandle> AcquireAsync(
        string key, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => AcquireAsync(key, new DistributedLockOptions { LeaseDuration = leaseDuration }, cancellationToken);
}
