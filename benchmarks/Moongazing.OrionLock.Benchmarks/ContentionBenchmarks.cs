using BenchmarkDotNet.Attributes;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// Measures what <c>N</c> concurrent blocking <c>AcquireAsync</c> callers on a SINGLE key cost, in
/// latency and - the number that actually matters - in backend round trips. Every other benchmark in
/// this suite measures the uncontended path, where the retry loop never runs; this one exists to put
/// a number on the retry loop itself.
/// </summary>
/// <remarks>
/// <para>
/// What it exposes: <c>DistributedLock.AcquireAsync</c> polls at a flat
/// <see cref="DistributedLockOptions.RetryInterval"/> with no jitter and no backoff, so every waiter
/// wakes on the same tick and the store sees an N-wide burst each interval. Only one of them can
/// win, so a queue of N waiters costs on the order of N*(N+1)/2 <c>TryAcquireAsync</c> calls - a
/// quadratic number of round trips to hand one key around N times. Against the in-process provider
/// each call is nanoseconds, so the latency column alone would never show this; the reported
/// provider-call count does.
/// </para>
/// <para>
/// Each waiter gets its OWN <see cref="DistributedLock"/> instance over a SHARED provider. This is
/// deliberate: reentrancy is tracked per lock instance, so N waiters sharing one instance would
/// collapse into nested handles and never contend at all. Separate instances model what contention
/// really is - N processes racing for one key in one store.
/// </para>
/// <para>
/// A gate handle holds the key until every waiter has registered at least one failed attempt, so the
/// burst is deterministic instead of depending on how fast the thread pool ramps. The gate adds
/// exactly one acquire and one release call per operation to the reported totals.
/// </para>
/// </remarks>
[MultiRuntimeConfig]
public class ContentionBenchmarks
{
    private const string Key = "bench-contended-key";

    private CountingLockProvider provider = null!;
    private DistributedLock gateLock = null!;
    private DistributedLock[] waiterLocks = null!;
    private DistributedLockOptions options = null!;
    private long operations;

    /// <summary>How many callers race for the one key.</summary>
    [Params(2, 8, 64, 256)]
    public int Waiters { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        provider = new CountingLockProvider(new BenchInMemoryLockProvider());
        gateLock = new DistributedLock(provider);
        waiterLocks = new DistributedLock[Waiters];
        for (var i = 0; i < Waiters; i++)
        {
            waiterLocks[i] = new DistributedLock(provider);
        }

        options = new DistributedLockOptions
        {
            // The watchdog is irrelevant here and would add unrelated renew traffic to the counts.
            AutoRenew = false,
            LeaseDuration = TimeSpan.FromSeconds(30),
            // 1 ms rather than the 250 ms default purely to keep wall time tractable: at the real
            // default, Waiters = 256 would serialize into more than a minute per operation and blow
            // through WaitTimeout. This scales the clock only - the provider-call COUNT is
            // interval-independent, because each poll round still hands the key to exactly one
            // waiter regardless of how long the round takes. The lockstep shape the flat interval
            // produces is identical; it just plays 250x faster.
            RetryInterval = TimeSpan.FromMilliseconds(1),
            // A timeout throws, which would abort the measurement rather than report it. Contention
            // here is bounded (every winner releases immediately), so this is never reached.
            WaitTimeout = TimeSpan.FromMinutes(5),
        };

        operations = 0;
        provider.Reset();
    }

    /// <summary>
    /// Prints the provider-call totals BenchmarkDotNet's own columns cannot carry (the benchmark runs
    /// in a child process, so a custom column in the host could not read these counters). The
    /// per-operation figures are the regression signal: they should stay flat when the retry loop
    /// gains jitter or backoff-with-notification, and they should FALL sharply if waiters stop
    /// polling in lockstep.
    /// </summary>
    [GlobalCleanup]
    public void ReportProviderCalls()
    {
        var ops = Interlocked.Read(ref operations);
        if (ops == 0)
        {
            return;
        }
        Console.WriteLine(
            $"// ContentionBenchmarks Waiters={Waiters}: ops={ops}, " +
            $"TryAcquireAsync/op={provider.AcquireCalls / (double)ops:F1}, " +
            $"ReleaseAsync/op={provider.ReleaseCalls / (double)ops:F1} " +
            $"(includes 1 acquire + 1 release for the gate handle)");
    }

    /// <summary>
    /// N waiters block on one key; each acquires and immediately releases, so the key is handed
    /// around N times. The measured time is the full drain; the reported call count is the burst.
    /// </summary>
    [Benchmark]
    public async Task ContendedAcquireRelease()
    {
        // Hold the key so no waiter can win until all of them are inside the retry loop.
        var gate = await gateLock.TryAcquireAsync(Key, options).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Gate handle could not take a free key.");
        var attemptsBefore = provider.AcquireCalls;

        var waiters = new Task[Waiters];
        for (var i = 0; i < Waiters; i++)
        {
            var waiterLock = waiterLocks[i];
            waiters[i] = Task.Run(() => AcquireAndReleaseAsync(waiterLock));
        }

        // Every waiter has now failed at least once, so the release below starts a clean N-wide race.
        while (provider.AcquireCalls - attemptsBefore < Waiters)
        {
            await Task.Yield();
        }

        await gate.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(waiters).ConfigureAwait(false);
        Interlocked.Increment(ref operations);
    }

    private async Task AcquireAndReleaseAsync(DistributedLock waiterLock)
    {
        var handle = await waiterLock.AcquireAsync(Key, options).ConfigureAwait(false);
        await handle.DisposeAsync().ConfigureAwait(false);
    }
}
