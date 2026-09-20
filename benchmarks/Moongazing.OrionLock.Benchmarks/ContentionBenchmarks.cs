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
/// A gate handle holds the key until every waiter has, for itself, seen one refused attempt, so the
/// burst is deterministic instead of depending on how fast the thread pool ramps. That barrier is
/// per waiter rather than an aggregate count of refusals: an aggregate cannot distinguish N waiters
/// that each failed once from one fast waiter that failed N times, which at the larger waiter counts
/// would let the gate open while some callers were still sitting in the thread-pool queue. The
/// scaffolding costs an exact, known number of provider calls per operation - one gate acquire plus
/// one barrier probe per waiter - and the report prints the totals both raw and net of it.
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
    private int barrierRemaining;
    private TaskCompletionSource barrierReached = null!;

    /// <summary>How many callers race for the one key.</summary>
    [Params(2, 8, 64, 256)]
    public int Waiters { get; set; }

    /// <summary>
    /// False is the pre-v2.1 waiter: the interface default, which polls. True is a backend that
    /// overrides <c>WaitForAcquireAsync</c> and parks the waiter until the lock frees, the way SQL
    /// Server's lock queue, a Redis release channel, an etcd watch and a ZooKeeper predecessor
    /// watch now do. Both run in one table so the before and the after cannot drift apart.
    /// </summary>
    [Params(false, true)]
    public bool EventDrivenWait { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        provider = new CountingLockProvider(new BenchInMemoryLockProvider(EventDrivenWait), forwardWait: EventDrivenWait);
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
        // The harness itself costs a known, exact number of provider calls per operation: one gate
        // acquire, plus one barrier probe per waiter. Report the raw total AND that total net of
        // the harness, so the retry-loop figure is not quietly inflated by the scaffolding that
        // makes the measurement deterministic.
        var harnessCalls = Waiters + 1;
        var rawPerOp = provider.AcquireCalls / (double)ops;
        var netPerOp = rawPerOp - harnessCalls;
        Console.WriteLine(
            $"// ContentionBenchmarks Waiters={Waiters} EventDrivenWait={EventDrivenWait}: ops={ops}, " +
            $"TryAcquireAsync/op={rawPerOp:F1} raw, {netPerOp:F1} net of harness " +
            $"({netPerOp / Waiters:F2} per waiter), " +
            $"(harness = 1 gate acquire + {Waiters} barrier probes), " +
            $"WaitForAcquireAsync/op={provider.WaitCalls / (double)ops:F1}, " +
            $"ReleaseAsync/op={provider.ReleaseCalls / (double)ops:F1} (includes the gate release)");
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

        barrierRemaining = Waiters;
        barrierReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var waiters = new Task[Waiters];
        for (var i = 0; i < Waiters; i++)
        {
            var waiterLock = waiterLocks[i];
            waiters[i] = Task.Run(() => ProbeThenAcquireAndReleaseAsync(waiterLock));
        }

        // Every waiter has now been scheduled AND has seen the key held for itself, so releasing
        // the gate starts a genuine N-wide race rather than a race with the thread-pool queue.
        await barrierReached.Task.ConfigureAwait(false);

        await gate.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAll(waiters).ConfigureAwait(false);
        Interlocked.Increment(ref operations);
    }

    private async Task ProbeThenAcquireAndReleaseAsync(DistributedLock waiterLock)
    {
        // Per-waiter barrier. An aggregate count of failed attempts cannot prove that every waiter
        // has started: with a 1 ms retry interval one already-running waiter contributes several
        // failures while other Task.Run items are still queued, so at the larger waiter counts the
        // gate would open before some callers had attempted at all. Those callers would then find
        // the key free, and both the call count and the drain time would be measuring thread-pool
        // scheduling instead of contention. Each waiter therefore signals for ITSELF, after its own
        // first refused attempt.
        var probe = await waiterLock.TryAcquireAsync(Key, options).ConfigureAwait(false);
        if (probe is not null)
        {
            // Never expected while the gate holds the key; release it rather than deadlock the run.
            await probe.DisposeAsync().ConfigureAwait(false);
        }
        if (Interlocked.Decrement(ref barrierRemaining) == 0)
        {
            barrierReached.TrySetResult();
        }

        var handle = await waiterLock.AcquireAsync(Key, options).ConfigureAwait(false);
        await handle.DisposeAsync().ConfigureAwait(false);
    }
}
