using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Internal;

/// <summary>
/// v0.4.0: the concrete handle for a held reader-writer hold. Mirrors
/// <see cref="DistributedLockHandle"/>: it runs a background watchdog that renews the lease at
/// <c>LeaseDuration / 3</c> intervals (when <see cref="DistributedLockOptions.AutoRenew"/> is set);
/// on renewal failure it flips <see cref="IsHeld"/> and trips <see cref="LostToken"/>. Disposing
/// stops the watchdog and releases the hold in its <see cref="LockMode"/>.
/// </summary>
public sealed class SharedExclusiveLockHandle : IDistributedLockHandle
{
    private readonly ISharedExclusiveLockProvider provider;
    private readonly string ownerToken;
    private readonly LockMode mode;
    private readonly TimeSpan leaseDuration;
    private readonly TimeSpan renewalGrace;
    private readonly CancellationTokenSource lostCts = new();
    private readonly CancellationTokenSource? watchdogCts;
    private readonly Task? watchdog;
    private readonly Func<DateTime> nowUtc;
    private DateTime lastSuccessfulRenewalUtc;
    private int disposed;
    private volatile bool isHeld = true;
    // Single-decrement guard for the orionlock.leases.held_concurrent gauge: both DisposeAsync and
    // the watchdog loss path call DecrementOnceIfHeld; Interlocked ensures exactly-once.
    private int decremented;
    private readonly long acquireTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Creates a handle and, when <see cref="DistributedLockOptions.AutoRenew"/> is set, starts the watchdog.</summary>
    public SharedExclusiveLockHandle(
        ISharedExclusiveLockProvider provider, string key, string ownerToken, LockMode mode,
        DistributedLockOptions options)
        : this(provider, key, ownerToken, mode, options, nowUtc: null)
    {
    }

    /// <summary>
    /// Test-only ctor exposing a clock hook so the fairness watchdog grace period can be driven
    /// deterministically. Production code uses the public overload which binds the clock to
    /// <see cref="DateTime.UtcNow"/>.
    /// </summary>
    internal SharedExclusiveLockHandle(
        ISharedExclusiveLockProvider provider, string key, string ownerToken, LockMode mode,
        DistributedLockOptions options, Func<DateTime>? nowUtc)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        this.provider = provider;
        Key = key;
        this.ownerToken = ownerToken;
        this.mode = mode;
        leaseDuration = options.LeaseDuration;
        renewalGrace = options.RenewalFailureGracePeriod ?? options.LeaseDuration;
        this.nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        lastSuccessfulRenewalUtc = this.nowUtc();

        if (options.AutoRenew)
        {
            watchdogCts = new CancellationTokenSource();
            watchdog = RenewLoopAsync(watchdogCts.Token);
        }
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <summary>The mode (shared or exclusive) this hold was acquired in.</summary>
    public LockMode Mode => mode;

    /// <inheritdoc />
    public bool IsHeld => isHeld;

    /// <inheritdoc />
    public CancellationToken LostToken => lostCts.Token;

    private async Task RenewLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromTicks(Math.Max(leaseDuration.Ticks / 3, TimeSpan.FromMilliseconds(10).Ticks));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);

                bool renewed;
                try
                {
                    renewed = await provider.TryRenewAsync(Key, ownerToken, mode, leaseDuration, ct).ConfigureAwait(false);
                }
                // Only OUR watchdog token cancelling is terminal (Dispose stopped the loop). An OCE
                // raised for any other reason - a provider that surfaces an unrelated cancellation -
                // must be handled as a transient renew failure, not silently stop renewals while
                // IsHeld stays true and LostToken never trips.
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // intentional: transient backend faults are recoverable
                catch
#pragma warning restore CA1031
                {
                    // Transient renewal failure. Same fairness-watchdog grace semantics as the
                    // exclusive handle: surrender once the grace period since the last successful
                    // renewal elapses so a stuck backend cannot perpetually deny new waiters.
                    if (nowUtc() - lastSuccessfulRenewalUtc > renewalGrace)
                    {
                        Surrender();
                        return;
                    }
                    continue;
                }

                if (!renewed)
                {
                    Surrender();
                    return;
                }
                lastSuccessfulRenewalUtc = nowUtc();
            }
        }
        catch (OperationCanceledException)
        {
            // watchdog stopped by Dispose
        }
    }

    private void Surrender()
    {
        isHeld = false;
        OrionLockDiagnostics.RecordLeaseLost();
        DecrementOnceIfHeld();
        SafeCancelLost();
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

        if (isHeld
            && provider.LeaseDurationIsTtl
            && nowUtc() - lastSuccessfulRenewalUtc > leaseDuration)
        {
            OrionLockDiagnostics.RecordLeaseExpiredBeforeRelease();
        }

        isHeld = false;
        DecrementOnceIfHeld();

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
            await provider.ReleaseAsync(Key, ownerToken, mode, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort release; the lease expires on its own if this fails
        }

        lostCts.Dispose();
    }

    private void DecrementOnceIfHeld()
    {
        if (Interlocked.Exchange(ref decremented, 1) == 0)
        {
            OrionLockDiagnostics.DecrementLeasesHeld();
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(acquireTimestamp);
            OrionLockDiagnostics.RecordHandleHoldingDuration(elapsed.TotalMilliseconds);
        }
    }
}
