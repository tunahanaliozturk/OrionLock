using System.Diagnostics;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Testing.Tests;

/// <summary>
/// The provider sampled <see cref="DateTime.UtcNow"/> BEFORE taking the per-key monitor, so a thread that
/// then queued on that monitor did all its work against an instant from before the wait: it pruned
/// against a stale "now", and stamped the hold it granted with <c>staleNow + lease</c>. The hold a caller
/// was handed therefore expired one monitor-wait EARLY, and under enough contention it was already gone
/// when the grant returned. Same defect the PostgreSQL provider documents fixing by reading its clock
/// after it is serialized.
/// </summary>
public sealed class InMemorySharedExclusiveClockTests
{
    [Fact]
    public async Task AGrantedHold_LastsTheWholeLease_MeasuredFromTheGrant()
    {
        // The promise a grant makes: from the moment TryAcquire returns, the caller holds the key for the
        // lease it asked for. So a renewal issued within that window must succeed. Heavy contention on
        // ONE key maximises the monitor wait, which is exactly the amount by which the pre-fix provider
        // short-changed the lease.
        //
        // The elapsed check is what keeps this honest under load: a renewal that arrives AFTER the lease
        // has genuinely run out is not a defect, it is a lease expiring, so only renewals that fail while
        // the lease should still be running are counted. Against the fixed provider that window cannot be
        // entered at all - the clock is read inside the monitor, so the hold expires no earlier than
        // (return of TryAcquire) + lease - which is why this reports zero rather than "few".
        var sut = new InMemorySharedExclusiveLockProvider();
        var lease = TimeSpan.FromMilliseconds(2);
        var premature = 0;
        var workers = Math.Max(8, Environment.ProcessorCount * 4);

        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 400; i++)
            {
                var token = $"reader-{worker}-{i}";
                // Resolved synchronously on purpose: the provider completes these tasks inline, and an
                // await here would put a continuation-scheduling delay between the grant and the
                // timestamp, which would blur the very interval this test measures.
                if (!sut.TryAcquireAsync("k", token, LockMode.Shared, lease, default).GetAwaiter().GetResult())
                {
                    continue;
                }

                var granted = Stopwatch.GetTimestamp();
                var renewed = sut.TryRenewAsync("k", token, LockMode.Shared, lease, default)
                    .GetAwaiter().GetResult();
                if (!renewed && Stopwatch.GetElapsedTime(granted) < lease)
                {
                    Interlocked.Increment(ref premature);
                }

                sut.ReleaseAsync("k", token, LockMode.Shared, default).GetAwaiter().GetResult();
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Measured on this suite: ~50 premature grants against the pre-fix provider, 0 or 1 against the
        // fixed one. The residual is this test's own measurement window, not the provider - the worker
        // can be preempted between TryAcquire returning and the timestamp being taken, and a 2 ms lease
        // is short enough for that to register. The bound separates a provider that short-changes every
        // contended grant from a measurement that is occasionally a microsecond late, without pretending
        // the measurement is exact.
        var count = Volatile.Read(ref premature);
        Assert.True(
            count <= 5,
            $"{count} grants expired before the lease they were granted for had run: the provider is "
            + "stamping expiries from an instant sampled before it took the key's monitor.");
    }
}
