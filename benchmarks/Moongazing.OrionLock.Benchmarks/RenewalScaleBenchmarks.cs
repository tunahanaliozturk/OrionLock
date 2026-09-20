using BenchmarkDotNet.Attributes;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// Measures what holding <c>N</c> auto-renewing locks at once costs. The rest of the suite runs with
/// <c>AutoRenew = false</c> so the watchdog never starts; this class is the opposite case, and the
/// only one that puts a number on the renewal machinery.
/// </summary>
/// <remarks>
/// <para>
/// What it exposes: every held handle owns a private <c>Task</c>, a private
/// <see cref="CancellationTokenSource"/> and a private <c>Task.Delay</c> timer, and renews at exactly
/// <c>LeaseDuration / 3</c> with no jitter. A host holding N leases therefore carries N background
/// tasks and N timers, and because the interval is a fixed fraction of the lease with no spread, the
/// handles acquired together keep firing together - the store sees N renewals in one tick rather
/// than N renewals spread over the interval. The allocation column shows the per-handle overhead,
/// and the printed renewal count shows the traffic.
/// </para>
/// <para>
/// Each handle takes a distinct key, so nothing collapses into the reentrancy registry and every
/// handle really does run its own watchdog. The lease is deliberately short (60 ms, renewing every
/// 20 ms) so a 200 ms hold window produces roughly ten renewals per handle - the same shape a
/// 30-second lease produces over a five-minute hold, compressed into something a benchmark can run.
/// </para>
/// </remarks>
[MultiRuntimeConfig]
public class RenewalScaleBenchmarks
{
    /// <summary>How long each batch of handles is held before disposal.</summary>
    private static readonly TimeSpan HoldWindow = TimeSpan.FromMilliseconds(200);

    private CountingLockProvider provider = null!;
    private DistributedLock distributedLock = null!;
    private DistributedLockOptions options = null!;
    private string[] keys = null!;
    private long operations;
    private long windowRenewals;

    /// <summary>How many auto-renewing handles are held simultaneously.</summary>
    [Params(1, 100, 1000)]
    public int Handles { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        provider = new CountingLockProvider(new BenchInMemoryLockProvider());
        distributedLock = new DistributedLock(provider);
        options = new DistributedLockOptions
        {
            AutoRenew = true,
            // 60 ms lease => the watchdog's LeaseDuration / 3 lands on 20 ms, comfortably above the
            // 10 ms floor the renew loop clamps to, so the interval under test is the real formula
            // rather than the clamp.
            LeaseDuration = TimeSpan.FromMilliseconds(60),
        };

        keys = new string[Handles];
        for (var i = 0; i < Handles; i++)
        {
            keys[i] = $"bench-renew-key-{i}";
        }

        operations = 0;
        windowRenewals = 0;
        provider.Reset();
    }

    /// <summary>
    /// Prints the renewal traffic, which no BenchmarkDotNet column can carry (the benchmark runs in a
    /// child process). Per-handle renewals should stay near
    /// <c>HoldWindow / (LeaseDuration / 3)</c> and, because only the fixed hold window is counted,
    /// should be flat across every <c>Handles</c> value. A jittered or shared-timer renewal scheme
    /// should leave this figure intact while cutting the allocation column.
    /// </summary>
    [GlobalCleanup]
    public void ReportRenewals()
    {
        var ops = Interlocked.Read(ref operations);
        if (ops == 0)
        {
            return;
        }
        var perOp = Interlocked.Read(ref windowRenewals) / (double)ops;
        Console.WriteLine(
            $"// RenewalScaleBenchmarks Handles={Handles}: ops={ops}, " +
            $"TryRenewAsync/op={perOp:F1}, per handle={perOp / Handles:F2} " +
            $"(hold window only) over a {HoldWindow.TotalMilliseconds:F0} ms hold at a " +
            $"{options.LeaseDuration.TotalMilliseconds / 3:F0} ms renewal interval");
    }

    /// <summary>
    /// Acquire N auto-renewing handles, hold them all for a fixed window while their watchdogs run,
    /// then dispose them.
    /// </summary>
    /// <remarks>
    /// Renewals are counted across the HOLD WINDOW ONLY, between a snapshot taken once every handle
    /// exists and a snapshot taken before the first disposal. A watchdog starts the moment its handle
    /// is constructed, so a handle acquired early is already renewing while later ones are still
    /// being created, and it keeps renewing while earlier ones are being disposed. Counting the whole
    /// method would therefore fold an N-dependent acquisition and disposal window into the figure and
    /// make the per-handle number at 1000 incomparable with the one at 1. The window delta is the
    /// same 200 ms for every N, so the per-handle figures can be compared directly.
    /// </remarks>
    [Benchmark]
    public async Task HoldAutoRenewingHandles()
    {
        var handles = new IDistributedLockHandle[Handles];
        for (var i = 0; i < Handles; i++)
        {
            handles[i] = await distributedLock.AcquireAsync(keys[i], options).ConfigureAwait(false);
        }

        // Every handle now exists and every watchdog is running: the window starts here, not at the
        // top of the method.
        var renewsBeforeWindow = provider.RenewCalls;
        await Task.Delay(HoldWindow).ConfigureAwait(false);
        Interlocked.Add(ref windowRenewals, provider.RenewCalls - renewsBeforeWindow);

        for (var i = 0; i < Handles; i++)
        {
            await handles[i].DisposeAsync().ConfigureAwait(false);
        }

        Interlocked.Increment(ref operations);
    }
}
