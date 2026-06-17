using BenchmarkDotNet.Attributes;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// Measures the end-to-end uncontended acquire-and-release cost of the real
/// <see cref="DistributedLock"/> over an in-process provider. With the backend reduced to a single
/// concurrent-dictionary operation and <c>AutoRenew = false</c> so the renewal watchdog never
/// starts, what remains is the pure orchestration cost: owner-token generation, reentrancy
/// registration, handle allocation, the held-concurrent gauge, and disposal. This is the abstraction
/// floor a caller pays on top of whatever the chosen backend round-trip costs in production.
/// </summary>
[MultiRuntimeConfig]
public class DistributedLockAcquireBenchmarks
{
    private DistributedLock distributedLock = null!;
    private DistributedLockOptions options = null!;
    private const string Key = "bench-acquire-key";

    [GlobalSetup]
    public void Setup()
    {
        distributedLock = new DistributedLock(new BenchInMemoryLockProvider());
        // AutoRenew off so the steady-state acquire cost is measured without the watchdog task.
        options = new DistributedLockOptions { AutoRenew = false };
    }

    /// <summary>Non-blocking acquire of a free key, then dispose to release. The happy path.</summary>
    [Benchmark]
    public async Task TryAcquireAndRelease()
    {
        var handle = await distributedLock.TryAcquireAsync(Key, options).ConfigureAwait(false);
        if (handle is not null)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Blocking <c>AcquireAsync</c> over the same free key. Uncontended, so it succeeds on the first
    /// attempt without ever hitting the retry delay, but it still pays for the Activity span, the
    /// acquire-duration / attempt-count metrics, and the FIFO no-op that the non-blocking path skips.
    /// </summary>
    [Benchmark]
    public async Task AcquireAndRelease()
    {
        var handle = await distributedLock.AcquireAsync(Key, options).ConfigureAwait(false);
        await handle.DisposeAsync().ConfigureAwait(false);
    }
}
