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
    private readonly TimeSpan renewalGrace;
    private readonly CancellationTokenSource lostCts = new();
    private readonly CancellationTokenSource? watchdogCts;
    private readonly Task? watchdog;
    private readonly Func<DateTime> nowUtc;
    private DateTime lastSuccessfulRenewalUtc;
    private int disposed;
    private volatile bool isHeld = true;

    /// <summary>Creates a handle and, when <see cref="DistributedLockOptions.AutoRenew"/> is set, starts the watchdog.</summary>
    public DistributedLockHandle(
        IDistributedLockProvider provider, string key, string ownerToken, DistributedLockOptions options)
        : this(provider, key, ownerToken, options, nowUtc: null)
    {
    }

    /// <summary>
    /// Test-only ctor exposing a clock hook so the fairness watchdog grace period can be
    /// driven deterministically. Production code uses the 4-arg overload which binds the
    /// clock to <see cref="DateTime.UtcNow"/>.
    /// </summary>
    internal DistributedLockHandle(
        IDistributedLockProvider provider, string key, string ownerToken, DistributedLockOptions options,
        Func<DateTime>? nowUtc)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        this.provider = provider;
        Key = key;
        this.ownerToken = ownerToken;
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
#pragma warning disable CA1031 // intentional: transient backend faults are recoverable; failure is recorded by MeasuringLockProvider
                catch
#pragma warning restore CA1031
                {
                    // Transient renewal failure (network blip, backend timeout). The failure
                    // counter is recorded by MeasuringLockProvider before the exception bubbles
                    // up here. v0.3.10 fairness watchdog: if exceptions keep firing past the
                    // RenewalFailureGracePeriod since the last successful renewal, treat as
                    // confirmed lost so a stuck backend cannot perpetually deny new waiters
                    // by leaving the lease unreleasable. The backend's TTL has almost
                    // certainly expired by now anyway.
                    if (nowUtc() - lastSuccessfulRenewalUtc > renewalGrace)
                    {
                        // v0.3.11: distinguish a fairness-watchdog auto-release from a
                        // backend-confirmed loss by incrementing the
                        // grace_period_exhausted counter IN ADDITION to leases.lost.
                        isHeld = false;
                        OrionLockDiagnostics.LeasesLost.Add(1);
                        OrionLockDiagnostics.LeasesGraceExhausted.Add(1);
                        SafeCancelLost();
                        return;
                    }
                    continue;
                }

                if (!renewed)
                {
                    isHeld = false;
                    OrionLockDiagnostics.LeasesLost.Add(1);
                    SafeCancelLost();
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
