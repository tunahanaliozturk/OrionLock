using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Testing.Tests;

/// <summary>
/// The provider sampled <see cref="DateTime.UtcNow"/> BEFORE taking the per-key monitor, so a thread that
/// then waited on that monitor did all its work against an instant from before the wait: it pruned
/// against a stale "now", and stamped the hold it granted with <c>staleNow + lease</c>. Under contention
/// that hands a caller a hold which is already expired the moment it is granted. Same defect the
/// PostgreSQL provider documents fixing by reading the clock after it is serialized.
/// </summary>
public sealed class InMemorySharedExclusiveClockTests
{
    [Fact]
    public async Task AGrantedHold_IsNeverAlreadyExpired_UnderContention()
    {
        // Short lease + heavy contention on ONE key: every thread queues on the same monitor, so the gap
        // between sampling the clock and acting on it is exactly the wait this test maximises. The
        // assertion is the promise the grant makes - if TryAcquire says yes, the caller holds the key, so
        // an immediate renew of that same hold must also say yes. A hold stamped from before the monitor
        // wait can already be gone by the time it is handed over, and that renew finds nothing.
        var sut = new InMemorySharedExclusiveLockProvider();
        var lease = TimeSpan.FromMilliseconds(2);
        var failures = 0;

        var workers = Enumerable.Range(0, 32).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var token = $"reader-{worker}-{i}";
                if (!await sut.TryAcquireAsync("k", token, LockMode.Shared, lease, default))
                {
                    continue;
                }

                if (!await sut.TryRenewAsync("k", token, LockMode.Shared, lease, default))
                {
                    Interlocked.Increment(ref failures);
                }

                await sut.ReleaseAsync("k", token, LockMode.Shared, default);
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.Equal(0, Volatile.Read(ref failures));
    }
}
