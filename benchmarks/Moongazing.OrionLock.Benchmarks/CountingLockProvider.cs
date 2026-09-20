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

    /// <summary>Wraps <paramref name="inner"/>, counting every call forwarded to it.</summary>
    public CountingLockProvider(IDistributedLockProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
    }

    /// <summary>Total <see cref="TryAcquireAsync"/> calls since the last <see cref="Reset"/>.</summary>
    public long AcquireCalls => Interlocked.Read(ref acquireCalls);

    /// <summary>Total <see cref="TryRenewAsync"/> calls since the last <see cref="Reset"/>.</summary>
    public long RenewCalls => Interlocked.Read(ref renewCalls);

    /// <summary>Total <see cref="ReleaseAsync"/> calls since the last <see cref="Reset"/>.</summary>
    public long ReleaseCalls => Interlocked.Read(ref releaseCalls);

    /// <summary>Zeroes every counter. Called from <c>[GlobalSetup]</c> so each parameter set is clean.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref acquireCalls, 0);
        Interlocked.Exchange(ref renewCalls, 0);
        Interlocked.Exchange(ref releaseCalls, 0);
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
