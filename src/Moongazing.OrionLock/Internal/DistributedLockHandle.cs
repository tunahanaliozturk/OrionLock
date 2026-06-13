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
    // v0.3.19 streak of consecutive renewal failures since the last successful renewal.
    // Recorded as a histogram sample when the streak ends (either by a successful
    // renewal OR by surrender) so operators see the FULL distribution of backend
    // flakiness shape.
    private int consecutiveRenewalFailures;
    // v0.3.25 optional lifecycle observer; null when no consumer registration.
    private readonly ILockEventObserver? eventObserver;
    private int disposed;
    private volatile bool isHeld = true;
    // v0.3.13 single-decrement guard for the orionlock.leases.held_concurrent gauge.
    // Both DisposeAsync AND the watchdog loss paths call DecrementOnceIfHeld; Interlocked
    // ensures exactly-once decrement regardless of who runs first.
    private int decremented;
    // v0.3.25 fix (codex P2 + coderabbit Major): single-fire guard for the terminal
    // lifecycle observer callbacks (OnReleased / OnLeaseLost). Under an AutoRenew
    // dispose-vs-watchdog race, DisposeAsync could read isHeld=true and fire OnReleased
    // while the watchdog concurrently observed renewal=false and fired OnLeaseLost for
    // the SAME handle. Interlocked.Exchange ensures exactly ONE terminal event wins,
    // mirroring the decremented guard which only protected metrics.
    private int terminalFired;
    // v0.3.14: capture acquire time so the holding-duration histogram can be recorded
    // exactly once when the handle's lifecycle ends.
    private readonly long acquireTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>
    /// v0.3.25: fire the terminal lifecycle observer callback exactly once. Returns
    /// true when this caller won the race (and the callback was invoked); false when a
    /// concurrent path already fired a terminal event for this handle.
    /// </summary>
    private bool TryFireTerminalReleased()
    {
        if (Interlocked.Exchange(ref terminalFired, 1) != 0)
        {
            return false;
        }
        eventObserver.SafeOnReleased(Key);
        return true;
    }

    private bool TryFireTerminalLost()
    {
        if (Interlocked.Exchange(ref terminalFired, 1) != 0)
        {
            return false;
        }
        eventObserver.SafeOnLeaseLost(Key);
        return true;
    }

    /// <summary>Creates a handle and, when <see cref="DistributedLockOptions.AutoRenew"/> is set, starts the watchdog.</summary>
    public DistributedLockHandle(
        IDistributedLockProvider provider, string key, string ownerToken, DistributedLockOptions options)
        : this(provider, key, ownerToken, options, nowUtc: null, eventObserver: null)
    {
    }

    /// <summary>
    /// v0.3.25 overload that wires the optional <see cref="ILockEventObserver"/> so the
    /// handle can fire <c>OnLeaseLost</c> / <c>OnReleased</c> lifecycle callbacks.
    /// </summary>
    public DistributedLockHandle(
        IDistributedLockProvider provider, string key, string ownerToken, DistributedLockOptions options,
        ILockEventObserver? eventObserver)
        : this(provider, key, ownerToken, options, nowUtc: null, eventObserver)
    {
    }

    /// <summary>
    /// Test-only ctor exposing a clock hook so the fairness watchdog grace period can be
    /// driven deterministically. Production code uses the public overloads which bind the
    /// clock to <see cref="DateTime.UtcNow"/>.
    /// </summary>
    internal DistributedLockHandle(
        IDistributedLockProvider provider, string key, string ownerToken, DistributedLockOptions options,
        Func<DateTime>? nowUtc, ILockEventObserver? eventObserver = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        this.provider = provider;
        Key = key;
        this.ownerToken = ownerToken;
        leaseDuration = options.LeaseDuration;
        renewalGrace = options.RenewalFailureGracePeriod ?? options.LeaseDuration;
        this.nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        this.eventObserver = eventObserver is NullLockEventObserver ? null : eventObserver;
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
                    consecutiveRenewalFailures++;
                    if (nowUtc() - lastSuccessfulRenewalUtc > renewalGrace)
                    {
                        // v0.3.11: distinguish a fairness-watchdog auto-release from a
                        // backend-confirmed loss by incrementing the
                        // grace_period_exhausted counter IN ADDITION to leases.lost.
                        // v0.3.19: record the final streak length so operators see how
                        // many failures preceded the surrender.
                        OrionLockDiagnostics.RecordConsecutiveRenewalFailures(consecutiveRenewalFailures);
                        isHeld = false;
                        OrionLockDiagnostics.RecordLeaseLost();
                        OrionLockDiagnostics.RecordLeaseGraceExhausted();
                        // v0.3.25: lifecycle observer - grace-exhausted surrender IS a
                        // lease loss. Single-fire guard prevents a released+lost
                        // double-fire under a dispose race.
                        TryFireTerminalLost();
                        DecrementOnceIfHeld();
                        SafeCancelLost();
                        return;
                    }
                    continue;
                }

                if (!renewed)
                {
                    // v0.3.19: surrender path - record the streak length.
                    OrionLockDiagnostics.RecordConsecutiveRenewalFailures(consecutiveRenewalFailures + 1);
                    isHeld = false;
                    OrionLockDiagnostics.RecordLeaseLost();
                    // v0.3.25: lifecycle observer - backend-confirmed loss. Single-fire
                    // guard prevents a released+lost double-fire under a dispose race.
                    TryFireTerminalLost();
                    DecrementOnceIfHeld();
                    SafeCancelLost();
                    return;
                }
                // v0.3.19: success path - record the recovered streak (if any) and reset.
                if (consecutiveRenewalFailures > 0)
                {
                    OrionLockDiagnostics.RecordConsecutiveRenewalFailures(consecutiveRenewalFailures);
                    consecutiveRenewalFailures = 0;
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

        // v0.3.21 expired_before_release detection: if the handle's lease has elapsed
        // by the time DisposeAsync runs, the caller held it longer than the configured
        // lease (or the watchdog stopped renewing for any reason). Fire the counter
        // BEFORE we mutate isHeld so the read is meaningful. Suppressed for session-
        // scoped backends (Postgres advisory locks, SQL Server sp_getapplock) where
        // lease duration is not a wall-clock TTL - holding past LeaseDuration is
        // legitimate on those providers, so emitting would produce false positives.
        if (isHeld
            && provider.LeaseDurationIsTtl
            && nowUtc() - lastSuccessfulRenewalUtc > leaseDuration)
        {
            OrionLockDiagnostics.RecordLeaseExpiredBeforeRelease();
        }
        // v0.3.25: lifecycle observer - a normal dispose while still held is a
        // 'released' event. The single-fire guard (TryFireTerminalReleased) makes this
        // mutually exclusive with the watchdog's OnLeaseLost: if the watchdog already
        // surrendered the lease (even mid-dispose race), terminalFired is already set
        // and this is a no-op. Conversely if dispose wins, a subsequent watchdog loss
        // observation is suppressed. Exactly one terminal event per handle.
        if (isHeld)
        {
            TryFireTerminalReleased();
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
            await provider.ReleaseAsync(Key, ownerToken, CancellationToken.None).ConfigureAwait(false);
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
            // v0.3.14: emit the held duration alongside the decrement so the histogram
            // captures BOTH normal-dispose and watchdog-loss lifecycles. Stopwatch ticks
            // avoid clock-adjust skew that DateTime.UtcNow would introduce on long holds.
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(acquireTimestamp);
            OrionLockDiagnostics.RecordHandleHoldingDuration(elapsed.TotalMilliseconds);
        }
    }
}
