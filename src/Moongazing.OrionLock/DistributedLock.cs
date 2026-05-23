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
    private readonly ReentrancyRegistry reentrancy = new();

    /// <summary>Creates a lock over the given backend provider.</summary>
    public DistributedLock(IDistributedLockProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
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

    /// <summary>Acquires the lock using a lease of <paramref name="leaseDuration"/> and default wait/retry.</summary>
    public Task<IDistributedLockHandle> AcquireAsync(
        string key, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => AcquireAsync(key, new DistributedLockOptions { LeaseDuration = leaseDuration }, cancellationToken);
}
