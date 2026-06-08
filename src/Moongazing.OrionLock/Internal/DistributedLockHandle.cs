using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Internal;

/// <summary>
/// The concrete lock handle. Runs a background watchdog that renews the lease at
/// <c>LeaseDuration / 3</c> intervals; on renewal failure it flips <see cref="IsHeld"/> and
/// trips <see cref="LostToken"/>. Disposing stops the watchdog and releases the lock.
/// </summary>
public sealed class DistributedLockHandle : IDistributedLockHandle
{
    private readonly IDistributedLockProvider provider;
    private readonly string ownerToken;
    private readonly TimeSpan leaseDuration;
    private readonly CancellationTokenSource lostCts = new();
    private readonly CancellationTokenSource? watchdogCts;
    private readonly Task? watchdog;
    private int disposed;
    private volatile bool isHeld = true;

    /// <summary>Creates a handle and, when <see cref="DistributedLockOptions.AutoRenew"/> is set, starts the watchdog.</summary>
    public DistributedLockHandle(
        IDistributedLockProvider provider, string key, string ownerToken, DistributedLockOptions options)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        this.provider = provider;
        Key = key;
        this.ownerToken = ownerToken;
        leaseDuration = options.LeaseDuration;

        if (options.AutoRenew)
        {
            watchdogCts = new CancellationTokenSource();
            watchdog = RenewLoopAsync(watchdogCts.Token);
        }
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public bool IsHeld => isHeld;

    /// <inheritdoc />
    public CancellationToken LostToken => lostCts.Token;

    private async Task RenewLoopAsync(CancellationToken ct)
    {
        // Renew at one third of the lease so a single transient failure does not lose the lease.
        var interval = TimeSpan.FromTicks(Math.Max(leaseDuration.Ticks / 3, TimeSpan.FromMilliseconds(10).Ticks));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);

                bool renewed;
                try
                {
                    renewed = await provider.TryRenewAsync(Key, ownerToken, leaseDuration, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // Transient renewal failure (network blip, backend timeout). The failure
                    // counter is recorded by MeasuringLockProvider before the exception bubbles
                    // up here, so the catch only needs to treat it as renewed=false and let the
                    // next renewal interval run. The watchdog will trip LostToken on a subsequent
                    // confirmed loss.
                    renewed = false;
                }

                if (!renewed)
                {
                    isHeld = false;
                    OrionLockDiagnostics.LeasesLost.Add(1);
                    SafeCancelLost();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // watchdog stopped by Dispose
        }
    }

    private void SafeCancelLost()
    {
        try { lostCts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        isHeld = false;

        if (watchdogCts is not null)
        {
            await watchdogCts.CancelAsync().ConfigureAwait(false);
            if (watchdog is not null)
            {
                try { await watchdog.ConfigureAwait(false); }
                catch { /* watchdog faults are not actionable on dispose */ }
            }
            watchdogCts.Dispose();
        }

        try
        {
            await provider.ReleaseAsync(Key, ownerToken, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort release; the lease expires on its own if this fails
        }

        lostCts.Dispose();
    }
}
