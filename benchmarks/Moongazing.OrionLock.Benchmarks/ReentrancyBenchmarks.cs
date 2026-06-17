using BenchmarkDotNet.Attributes;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// Measures the same-process reentrancy fast path of <see cref="DistributedLock"/>. When a key is
/// already held by this lock instance, a re-acquisition must collapse into a counted nested handle
/// instead of issuing a second backend call. This benchmark holds an outer lease for the duration of
/// the measurement, then repeatedly acquires and releases a nested handle on the same key: every
/// inner acquire should hit the reentrancy registry and never touch the provider. It quantifies how
/// cheap a recursive critical section is, which matters for code that re-enters a held lock deep in a
/// call chain (the scenario the reentrancy-depth metrics exist to surface).
/// </summary>
[MultiRuntimeConfig]
public class ReentrancyBenchmarks
{
    private DistributedLock distributedLock = null!;
    private DistributedLockOptions options = null!;
    private IDistributedLockHandle outerHandle = null!;
    private const string Key = "bench-reentrant-key";

    [GlobalSetup]
    public async Task Setup()
    {
        distributedLock = new DistributedLock(new BenchInMemoryLockProvider());
        options = new DistributedLockOptions { AutoRenew = false };
        // Hold the outer lease for the whole run so every measured acquire is a nested re-entry
        // that collapses in the reentrancy registry instead of calling the backend.
        outerHandle = (await distributedLock.TryAcquireAsync(Key, options).ConfigureAwait(false))!;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await outerHandle.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Acquire and release a nested handle on an already-held key (no backend call).</summary>
    [Benchmark]
    public async Task NestedReentrantAcquireRelease()
    {
        var nested = await distributedLock.TryAcquireAsync(Key, options).ConfigureAwait(false);
        if (nested is not null)
        {
            await nested.DisposeAsync().ConfigureAwait(false);
        }
    }
}
