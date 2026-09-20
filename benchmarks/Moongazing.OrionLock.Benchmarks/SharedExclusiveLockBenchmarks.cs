using BenchmarkDotNet.Attributes;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// Measures <see cref="SharedExclusiveLock"/>, the reader-writer path, which had no benchmark at all
/// before this class. It is a separate orchestration from <see cref="DistributedLock"/> - its own
/// retry loop, no reentrancy registry, no FIFO coordinator - so none of the exclusive-lock numbers
/// transfer to it.
/// </summary>
/// <remarks>
/// <para>
/// Three shapes are covered. The uncontended shared acquire is the floor a reader pays. The
/// shared-on-shared acquire is what a second concurrent reader costs while a first one holds, which
/// is the case a reader-writer lock exists to make cheap. The writer-waiting-on-readers case is the
/// handoff: a writer is refused while a reader holds, records the backend's pending-writer
/// reservation, polls, and takes the key once the reader drains.
/// </para>
/// <para>
/// The backend is the shipped <see cref="InMemorySharedExclusiveLockProvider"/> rather than a
/// bench-local stand-in, because the writer-fairness behaviour under test (the pending-writer
/// reservation that holds off new readers so a writer is not starved) lives in the provider, and a
/// simplified fake would measure a different algorithm.
/// </para>
/// </remarks>
[MultiRuntimeConfig]
public class SharedExclusiveLockBenchmarks
{
    private const string SharedKey = "bench-rw-shared-key";
    private const string SharedOnSharedKey = "bench-rw-shared-on-shared-key";
    private const string HandoffKey = "bench-rw-handoff-key";

    private SharedExclusiveLock rwLock = null!;
    private DistributedLockOptions options = null!;
    private DistributedLockOptions writerOptions = null!;
    private IDistributedLockHandle outerReader = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        rwLock = new SharedExclusiveLock(new InMemorySharedExclusiveLockProvider());
        options = new DistributedLockOptions { AutoRenew = false, LeaseDuration = TimeSpan.FromSeconds(30) };
        writerOptions = new DistributedLockOptions
        {
            AutoRenew = false,
            LeaseDuration = TimeSpan.FromSeconds(30),
            // 1 ms rather than the 250 ms default so the handoff measurement is the handoff and not
            // a quarter second of sleep. The shape is what matters: the writer learns the readers
            // have drained only by polling, so its wake-up latency is one retry interval no matter
            // how the interval is set.
            RetryInterval = TimeSpan.FromMilliseconds(1),
            WaitTimeout = TimeSpan.FromMinutes(1),
        };

        // Held for the whole run so SharedOnShared always measures a SECOND reader joining an
        // existing one rather than a first reader taking a free key. The lease is an hour rather
        // than the 30 s used elsewhere BECAUSE the watchdog is off: a 30 s lease would quietly
        // expire partway through a longer run, the provider would prune the holder, and the
        // benchmark would go on reporting an uncontended acquire under a shared-on-shared name.
        var outerOptions = new DistributedLockOptions { AutoRenew = false, LeaseDuration = TimeSpan.FromHours(1) };
        outerReader = await rwLock.AcquireSharedAsync(SharedOnSharedKey, outerOptions).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup() => await outerReader.DisposeAsync().ConfigureAwait(false);

    /// <summary>A single reader taking and releasing a free key. The floor a read hold costs.</summary>
    [Benchmark(Baseline = true)]
    public async Task SharedAcquireRelease()
    {
        var handle = await rwLock.AcquireSharedAsync(SharedKey, options).ConfigureAwait(false);
        await handle.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A second reader joining a key a first reader already holds. This must not contend - it is the
    /// whole reason to reach for a reader-writer lock instead of an exclusive one - so it should
    /// land within noise of the uncontended baseline.
    /// </summary>
    [Benchmark]
    public async Task SharedOnSharedAcquireRelease()
    {
        var handle = await rwLock.AcquireSharedAsync(SharedOnSharedKey, options).ConfigureAwait(false);
        await handle.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A writer blocked behind a reader, then granted once the reader drains. The writer's first
    /// attempt runs inline before <c>AcquireExclusiveAsync</c> returns its task, so it is guaranteed
    /// to be refused (and to have recorded the pending-writer reservation) before the reader below
    /// releases. What is left to measure is the poll-driven wake-up: the writer finds out the key is
    /// free only on its next retry tick, because nothing notifies it.
    /// </summary>
    /// <remarks>
    /// Expect this row to come out near one operating-system timer tick (about 15.6 ms on Windows)
    /// rather than near the 1 ms retry interval, because that is what a <c>Task.Delay</c> shorter
    /// than a tick actually sleeps for. That is the finding, not an artefact: shrinking
    /// <see cref="DistributedLockOptions.RetryInterval"/> below a tick buys nothing, so handoff
    /// latency cannot be tuned down - only a notification instead of a poll can move it.
    /// </remarks>
    [Benchmark]
    public async Task ExclusiveWaitingOnReaders()
    {
        var reader = await rwLock.AcquireSharedAsync(HandoffKey, options).ConfigureAwait(false);
        var writerAcquire = rwLock.AcquireExclusiveAsync(HandoffKey, writerOptions);

        await reader.DisposeAsync().ConfigureAwait(false);

        var writer = await writerAcquire.ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);
    }
}
