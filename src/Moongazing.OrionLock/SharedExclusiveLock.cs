using System.Diagnostics;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock;

/// <summary>
/// v0.4.0: the default <see cref="ISharedExclusiveLock"/>. Composes a backend
/// <see cref="ISharedExclusiveLockProvider"/> with a blocking-acquire retry loop and
/// lease-renewing handles, exactly as <see cref="DistributedLock"/> does for the exclusive-only
/// provider.
/// </summary>
/// <remarks>
/// <para>
/// Fairness / writer-starvation: this release polls the backend on the same retry interval the
/// exclusive lock uses. The FIFO waiter coordinator (<see cref="DistributedLockOptions.UseFifoWaiterCoordinator"/>)
/// is exclusive-only and is NOT consulted here, so under sustained shared-reader load a waiting
/// writer can in principle be starved if readers keep arriving and the backend always reports the
/// key shared-held. The in-memory backend mitigates this by reserving the key for a waiting writer
/// the moment the last shared holder drains; see the in-memory provider for the documented
/// pending-writer reservation behaviour. Cross-process fair RW ordering is a v0.4.x follow-up.
/// </para>
/// <para>
/// Reentrancy is not modelled for shared/exclusive holds in this release: each acquire takes a
/// fresh backend hold. A nested acquire of the same key in the same flow is treated as an
/// independent holder (a shared-on-shared nesting therefore works; a write-on-read or read-on-write
/// nesting on the same key in the same flow will block or fail like any other holder and can
/// deadlock if used that way).
/// </para>
/// </remarks>
public sealed class SharedExclusiveLock : ISharedExclusiveLock
{
    private readonly ISharedExclusiveLockProvider provider;

    /// <summary>Creates a reader-writer lock over the given backend provider.</summary>
    public SharedExclusiveLock(ISharedExclusiveLockProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
    }

    /// <inheritdoc />
    public Task<IDistributedLockHandle?> TryAcquireSharedAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => TryAcquireAsync(key, LockMode.Shared, options, cancellationToken);

    /// <inheritdoc />
    public Task<IDistributedLockHandle?> TryAcquireExclusiveAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => TryAcquireAsync(key, LockMode.Exclusive, options, cancellationToken);

    /// <inheritdoc />
    public Task<IDistributedLockHandle> AcquireSharedAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => AcquireAsync(key, LockMode.Shared, options, cancellationToken);

    /// <inheritdoc />
    public Task<IDistributedLockHandle> AcquireExclusiveAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
        => AcquireAsync(key, LockMode.Exclusive, options, cancellationToken);

    private Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, LockMode mode, DistributedLockOptions? options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();
        return TryAcquireAsync(key, Guid.NewGuid().ToString("N"), mode, options, cancellationToken);
    }

    private async Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, string ownerToken, LockMode mode, DistributedLockOptions options, CancellationToken cancellationToken)
    {
        var acquired = await provider
            .TryAcquireAsync(key, ownerToken, mode, options.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);

        if (!acquired)
        {
            return null;
        }

        var handle = new SharedExclusiveLockHandle(provider, key, ownerToken, mode, options);
        // Same held-concurrent gauge convention as the exclusive path: increment only when a real
        // backend hold is taken. The handle decrements exactly once on dispose / loss.
        OrionLockDiagnostics.IncrementLeasesHeld();
        return handle;
    }

    private async Task<IDistributedLockHandle> AcquireAsync(
        string key, LockMode mode, DistributedLockOptions? options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();

        var modeTag = mode == LockMode.Shared ? "shared" : "exclusive";
        using var activity = OrionLockDiagnostics.ActivitySource.StartActivity($"OrionLock.AcquireRW {key}");
        activity?.SetTag("orionlock.key", key);
        activity?.SetTag("orionlock.mode", modeTag);

        try
        {
            // Mint the owner token ONCE and reuse it across every poll attempt of this blocking
            // acquire. A fresh token per attempt would leak the in-memory provider's pending-writer
            // reservation: the reservation is keyed by owner token and only the same owner can clear
            // it on a successful retry, so a per-attempt token would leave a stale reservation that
            // denies new readers until its lease expires after this writer releases.
            var ownerToken = Guid.NewGuid().ToString("N");
            var deadline = Stopwatch.StartNew();
            var contended = false;
            while (true)
            {
                var handle = await TryAcquireAsync(key, ownerToken, mode, options, cancellationToken).ConfigureAwait(false);
                if (handle is not null)
                {
                    activity?.SetTag("orionlock.outcome", "acquired");
                    OrionLockDiagnostics.RecordAcquisition();
                    OrionLockDiagnostics.RecordAcquireDuration(deadline.Elapsed.TotalMilliseconds);
                    if (contended)
                    {
                        OrionLockDiagnostics.RecordContentionDuration(deadline.Elapsed.TotalMilliseconds);
                    }
                    return handle;
                }

                contended = true;
                OrionLockDiagnostics.RecordContention();

                var remaining = options.WaitTimeout - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    activity?.SetTag("orionlock.outcome", "timeout");
                    OrionLockDiagnostics.RecordAcquireTimeout(key);
                    throw new LockAcquisitionTimeoutException(key, deadline.Elapsed);
                }

                // Clamp the poll delay to the time left until WaitTimeout so a full RetryInterval
                // near the deadline cannot overshoot the caller's wait budget by up to one interval.
                var delay = options.RetryInterval < remaining ? options.RetryInterval : remaining;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("orionlock.outcome", "cancelled");
            OrionLockDiagnostics.RecordAcquireCancelled();
            throw;
        }
    }
}
