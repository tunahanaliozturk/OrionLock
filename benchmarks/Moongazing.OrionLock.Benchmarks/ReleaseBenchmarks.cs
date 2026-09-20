using BenchmarkDotNet.Attributes;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// Measures disposal on its own. Everywhere else in this suite the release path is fused to an
/// acquire in a single measured method, so its cost has never been separable - and the two halves
/// are not symmetric: acquire is one provider call, while disposing an auto-renewing handle has to
/// cancel the watchdog's <see cref="CancellationTokenSource"/> and AWAIT the watchdog task before it
/// can issue the release.
/// </summary>
/// <remarks>
/// <para>
/// The <c>AutoRenew</c> on/off pair is the point. With the watchdog off, dispose is the metric
/// bookkeeping plus one provider call. With it on, dispose additionally pays a cancel, a task
/// rendezvous with a watchdog currently parked in <c>Task.Delay</c>, and the CTS disposal. The gap
/// between the two rows is what every auto-renewing handle costs to put down, and it is paid inline
/// on the caller's path out of the <c>using</c> block.
/// </para>
/// <para>
/// The handle is acquired in <c>[IterationSetup]</c> so only the dispose is timed, which is why this
/// class runs with one invocation per iteration
/// (<see cref="MultiRuntimeConfigAttribute(bool)"/>): with the default unrolled invocation count the
/// setup would run once for many invocations and every invocation after the first would measure a
/// dispose of an already-disposed handle. The trade-off is a coarser timer, so read these rows as
/// the on/off DELTA rather than as an absolute nanosecond figure.
/// </para>
/// </remarks>
[MultiRuntimeConfig(singleInvocationPerIteration: true)]
public class ReleaseBenchmarks
{
    private const string AutoRenewKey = "bench-release-autorenew-key";
    private const string PlainKey = "bench-release-plain-key";

    private DistributedLock distributedLock = null!;
    private DistributedLockOptions autoRenewOptions = null!;
    private DistributedLockOptions plainOptions = null!;
    private IDistributedLockHandle autoRenewHandle = null!;
    private IDistributedLockHandle plainHandle = null!;

    [GlobalSetup]
    public void Setup()
    {
        distributedLock = new DistributedLock(new BenchInMemoryLockProvider());
        // A 30 s lease means the watchdog's first renewal is 10 s away, so it is guaranteed to be
        // parked in Task.Delay when dispose cancels it. That isolates the teardown cost from any
        // renewal work, which RenewalScaleBenchmarks measures separately.
        autoRenewOptions = new DistributedLockOptions { AutoRenew = true, LeaseDuration = TimeSpan.FromSeconds(30) };
        plainOptions = new DistributedLockOptions { AutoRenew = false, LeaseDuration = TimeSpan.FromSeconds(30) };
    }

    [IterationSetup(Target = nameof(DisposeAutoRenewingHandle))]
    public void AcquireAutoRenewingHandle() =>
        autoRenewHandle = distributedLock.TryAcquireAsync(AutoRenewKey, autoRenewOptions)
            .GetAwaiter().GetResult()!;

    [IterationSetup(Target = nameof(DisposePlainHandle))]
    public void AcquirePlainHandle() =>
        plainHandle = distributedLock.TryAcquireAsync(PlainKey, plainOptions)
            .GetAwaiter().GetResult()!;

    /// <summary>Dispose a handle whose watchdog is running: cancel, await the watchdog, release.</summary>
    [Benchmark]
    public async Task DisposeAutoRenewingHandle() => await autoRenewHandle.DisposeAsync().ConfigureAwait(false);

    /// <summary>Dispose a handle with no watchdog: metric bookkeeping plus one provider call.</summary>
    [Benchmark(Baseline = true)]
    public async Task DisposePlainHandle() => await plainHandle.DisposeAsync().ConfigureAwait(false);
}
