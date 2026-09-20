using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// A pass-through <see cref="IDistributedLockProvider"/> decorator that counts how many times the
/// core orchestration actually calls the backend. Latency alone hides the cost that matters for a
/// real store: a benchmark can look fast while issuing a quadratic number of round trips, because
/// the in-process provider answers each one in nanoseconds. Wrapping
/// <see cref="BenchInMemoryLockProvider"/> in this decorator turns "how many times would we have hit
/// Redis / etcd / ZooKeeper" into a number the contention and renewal benchmarks can report
/// alongside the timings.
/// </summary>
/// <remarks>
/// The counters are process-wide for the decorator instance and accumulate across every
/// BenchmarkDotNet invocation (pilot, warmup and workload alike). Benchmarks therefore divide by
/// their own operation count rather than reading an absolute figure, and reset in
/// <c>[GlobalSetup]</c> so each <c>[Params]</c> value starts from zero.
/// </remarks>
public sealed class CountingLockProvider : IDistributedLockProvider
{
    private readonly IDistributedLockProvider inner;
    private long acquireCalls;
    private long renewCalls;
    private long releaseCalls;
    private long waitCalls;

    private readonly bool forwardWait;

    /// <summary>Wraps <paramref name="inner"/>, counting every call forwarded to it.</summary>
    /// <param name="inner">The provider being measured.</param>
    /// <param name="forwardWait">
    /// True when <paramref name="inner"/> implements its own <c>WaitForAcquireAsync</c>: the wait
    /// is forwarded, and the inner provider's parked waiter issues no calls through this counter,
    /// which is the point of it.
    /// <para>
    /// False reproduces a provider that does NOT override the member - every release before v2.1.
    /// The poll then runs AT THIS LEVEL, so each retry is counted. Forwarding it instead would send
    /// the poll into the inner provider, where it would issue its retries directly against itself
    /// and this counter would report one call per waiter for a loop that made dozens - a benchmark
    /// measuring polling while reporting the numbers of something else.
    /// </para>
    /// </param>
    public CountingLockProvider(IDistributedLockProvider inner, bool forwardWait = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
        this.forwardWait = forwardWait;
    }

    /// <summary>Total <see cref="TryAcquireAsync"/> calls since the last <see cref="Reset"/>.</summary>
    public long AcquireCalls => Interlocked.Read(ref acquireCalls);

    /// <summary>Total <see cref="TryRenewAsync"/> calls since the last <see cref="Reset"/>.</summary>
    public long RenewCalls => Interlocked.Read(ref renewCalls);

    /// <summary>Total <see cref="ReleaseAsync"/> calls since the last <see cref="Reset"/>.</summary>
    public long ReleaseCalls => Interlocked.Read(ref releaseCalls);

    /// <summary>Total <see cref="WaitForAcquireAsync"/> calls since the last <see cref="Reset"/>.</summary>
    public long WaitCalls => Interlocked.Read(ref waitCalls);

    /// <summary>Zeroes every counter. Called from <c>[GlobalSetup]</c> so each parameter set is clean.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref acquireCalls, 0);
        Interlocked.Exchange(ref renewCalls, 0);
        Interlocked.Exchange(ref releaseCalls, 0);
        Interlocked.Exchange(ref waitCalls, 0);
    }

    /// <summary>
    /// Forwards the event-driven wait, or runs the poll here - see the constructor's
    /// <c>forwardWait</c>. A decorator that leaves this member out entirely silently falls back to
    /// the interface default and the inner provider's own wait becomes unreachable; that omission
    /// is a bug this codebase has already shipped once, in <c>MeasuringLockProvider</c>.
    /// </summary>
    public Task<LockAcquisition> WaitForAcquireAsync(
        string key, string ownerToken, TimeSpan leaseDuration, TimeSpan maxWait,
        LockWaitPolicy waitPolicy, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref waitCalls);
        return forwardWait
            ? inner.WaitForAcquireAsync(key, ownerToken, leaseDuration, maxWait, waitPolicy, cancellationToken)
            : DistributedLockProviderExtensions.PollUntilAcquiredAsync(
                this, key, ownerToken, leaseDuration, maxWait, waitPolicy.ToPollOptions(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref acquireCalls);
        return inner.TryAcquireAsync(key, ownerToken, leaseDuration, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref renewCalls);
        return inner.TryRenewAsync(key, ownerToken, leaseDuration, cancellationToken);
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref releaseCalls);
        return inner.ReleaseAsync(key, ownerToken, cancellationToken);
    }

    /// <inheritdoc />
    public bool LeaseDurationIsTtl => inner.LeaseDurationIsTtl;
}
