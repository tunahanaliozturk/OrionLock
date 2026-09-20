namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;
using Xunit;

// held_concurrent is a process-wide gauge and these tests assert an absolute zero after each
// lifecycle, so this collection must not run alongside anything else that takes a lease.
[CollectionDefinition(nameof(HandleSignalParityTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class HandleSignalParityTestsCollection { }
#pragma warning restore CA1711

/// <summary>
/// The README promises the reader-writer surface's lease, renewal, release and diagnostics semantics
/// mirror the exclusive <see cref="IDistributedLock"/>. They did not: a reader-writer hold emitted
/// neither the failure-streak, grace-exhausted and renewals-per-hold instruments nor a single
/// <see cref="ILockEventObserver"/> callback.
/// </summary>
/// <remarks>
/// Each lifecycle below is driven through all three hold kinds - exclusive, reader-writer shared and
/// reader-writer exclusive - and the ORDERED log of everything they emitted (instruments and observer
/// callbacks interleaved) is asserted identical, then asserted against the sequence the paths are
/// documented to produce. Asserting the order, not just the set, is what pins the deliberate
/// arrangements: grace_period_exhausted between lease.lost and the observer callback, and
/// expired_before_release ahead of the surrender. The next divergence fails here.
/// </remarks>
[Collection(nameof(HandleSignalParityTests))]
public sealed class HandleSignalParityTests
{
    private const string Key = "parity";

    private enum Renewal
    {
        /// <summary>The backend keeps granting the lease.</summary>
        Succeeds,

        /// <summary>The backend reports the hold is gone (renewal returns false).</summary>
        ReportsLost,

        /// <summary>The backend is unreachable and the renewal call throws.</summary>
        Throws,
    }

    private delegate IDistributedLockHandle HandleFactory(
        Renewal renewal, DistributedLockOptions options, Func<DateTime> clock, ILockEventObserver observer);

    private static readonly (string Name, HandleFactory Create)[] HoldKinds =
    [
        ("exclusive", static (renewal, options, clock, observer) => new DistributedLockHandle(
            new ScriptedExclusiveProvider(renewal), Key, "owner", options, clock, observer)),
        ("reader-writer shared", static (renewal, options, clock, observer) => new SharedExclusiveLockHandle(
            new ScriptedRwProvider(renewal), Key, "owner", LockMode.Shared, options, clock, observer)),
        ("reader-writer exclusive", static (renewal, options, clock, observer) => new SharedExclusiveLockHandle(
            new ScriptedRwProvider(renewal), Key, "owner", LockMode.Exclusive, options, clock, observer)),
    ];

    [Fact]
    public async Task A_renewed_hold_released_by_dispose_emits_the_same_signals_for_every_hold_kind()
    {
        var expected = new[]
        {
            "observer:released",
            "orion.lock.leases.held_concurrent",
            "orion.lock.handle.holding_duration",
            "orion.lock.handle.renewals_per_hold",
        };

        await AssertParityAsync(
            expected,
            Renewal.Succeeds,
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(60), AutoRenew = true },
            FrozenClock,
            waitForLoss: false);
    }

    [Fact]
    public async Task A_backend_confirmed_loss_emits_the_same_signals_for_every_hold_kind()
    {
        var expected = new[]
        {
            // The streak that ended in the surrender, recorded before the loss itself.
            "orion.lock.lease.renewal_failures_consecutive",
            "orion.lock.lease.lost",
            "observer:lost",
            "orion.lock.leases.held_concurrent",
            "orion.lock.handle.holding_duration",
            "orion.lock.handle.renewals_per_hold",
        };

        await AssertParityAsync(
            expected,
            Renewal.ReportsLost,
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(60), AutoRenew = true },
            FrozenClock,
            waitForLoss: true);
    }

    [Fact]
    public async Task An_exhausted_renewal_grace_emits_the_same_signals_for_every_hold_kind()
    {
        var expected = new[]
        {
            "orion.lock.lease.renewal_failures_consecutive",
            "orion.lock.lease.lost",
            // Deliberately between the loss counter and the observer callback: it qualifies the loss
            // as a fairness surrender rather than a backend-confirmed one.
            "orion.lock.lease.grace_period_exhausted",
            "observer:lost",
            "orion.lock.leases.held_concurrent",
            "orion.lock.handle.holding_duration",
            // This path does NOT emit the renewal count at surrender; the later dispose does.
            "orion.lock.handle.renewals_per_hold",
        };

        await AssertParityAsync(
            expected,
            Renewal.Throws,
            new DistributedLockOptions
            {
                LeaseDuration = TimeSpan.FromMilliseconds(60),
                AutoRenew = true,
                RenewalFailureGracePeriod = TimeSpan.FromMilliseconds(40),
            },
            ClockThatJumpsAfterTheFirstReading,
            waitForLoss: true);
    }

    [Fact]
    public async Task A_ttl_expiry_without_autorenew_emits_the_same_signals_for_every_hold_kind()
    {
        var expected = new[]
        {
            // Recorded before the surrender clears the held flag that the dispose-path check needs.
            "orion.lock.lease.expired_before_release",
            "orion.lock.lease.lost",
            "observer:lost",
            "orion.lock.leases.held_concurrent",
            "orion.lock.handle.holding_duration",
            "orion.lock.handle.renewals_per_hold",
        };

        await AssertParityAsync(
            expected,
            Renewal.Succeeds,
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(100), AutoRenew = false },
            FrozenClock,
            waitForLoss: true);
    }

    [Fact]
    public async Task A_blocking_reader_writer_acquire_records_its_attempt_count_and_tells_the_observer()
    {
        var log = new SignalLog();
        using var listener = StartListener(log);
        var observer = new LoggingObserver(log);

        var rwLock = new SharedExclusiveLock(new InMemorySharedExclusiveLockProvider(), observer);
        var handle = await rwLock.AcquireExclusiveAsync(
            "rw-acquire", new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(5), AutoRenew = false });
        await handle.DisposeAsync();

        Assert.Contains("orion.lock.acquire.attempt_count", log.Names);
        Assert.Contains("observer:acquired", log.Names);
        Assert.Contains("observer:released", log.Names);
    }

    [Fact]
    public async Task A_reader_writer_acquire_that_times_out_tells_the_observer_before_it_throws()
    {
        var log = new SignalLog();
        using var listener = StartListener(log);
        var observer = new LoggingObserver(log);

        var rwLock = new SharedExclusiveLock(new InMemorySharedExclusiveLockProvider(), observer);
        var options = new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(5),
            WaitTimeout = TimeSpan.FromMilliseconds(100),
            RetryInterval = TimeSpan.FromMilliseconds(20),
            AutoRenew = false,
        };

        await using var held = await rwLock.AcquireExclusiveAsync("rw-timeout", options);
        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(
            () => rwLock.AcquireExclusiveAsync("rw-timeout", options));

        Assert.Contains("observer:timeout", log.Names);
    }

    private static async Task AssertParityAsync(
        string[] expected, Renewal renewal, DistributedLockOptions options, Func<Func<DateTime>> clock, bool waitForLoss)
    {
        (string Kind, IReadOnlyList<string> Log)? first = null;
        foreach (var (name, create) in HoldKinds)
        {
            var log = await RunLifecycleAsync(create, renewal, options, clock(), waitForLoss);
            Assert.Equal(expected, log);
            first ??= (name, log);
            // Redundant with the line above while both match `expected`, but it is the assertion that
            // survives a future edit to `expected`: the three kinds must never drift apart.
            Assert.Equal(first.Value.Log, log);
        }
    }

    private static async Task<IReadOnlyList<string>> RunLifecycleAsync(
        HandleFactory create, Renewal renewal, DistributedLockOptions options, Func<DateTime> clock, bool waitForLoss)
    {
        var log = new SignalLog();
        using var listener = StartListener(log);

        // Stands in for the increment the lock performs when it takes a real backend hold; the handle
        // owns the matching decrement.
        OrionLockDiagnostics.IncrementLeasesHeld();
        Assert.Equal(1, Interlocked.Read(ref log.Gauge));
        log.Clear();

        var handle = create(renewal, options, clock, new LoggingObserver(log));
        if (waitForLoss)
        {
            var lost = new TaskCompletionSource();
            using var registration = handle.LostToken.Register(() => lost.TrySetResult());
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        else
        {
            // Long enough for several renewals at LeaseDuration / 3.
            await Task.Delay(120);
        }

        await handle.DisposeAsync();

        Assert.Equal(0, Interlocked.Read(ref log.Gauge));
        return log.Names;
    }

    /// <summary>A clock that never moves, so no lifecycle here can trip a wall-clock deadline by accident.</summary>
    private static Func<DateTime> FrozenClock()
    {
        var baseTime = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        return () => baseTime;
    }

    /// <summary>
    /// The handle takes the first reading in its constructor to stamp the last successful renewal;
    /// every later reading is an hour past it, so the FIRST failed renewal already exhausts any grace
    /// period. Deterministic where waiting on the wall clock would not be.
    /// </summary>
    private static Func<DateTime> ClockThatJumpsAfterTheFirstReading()
    {
        var baseTime = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var readings = 0;
        return () => Interlocked.Increment(ref readings) == 1 ? baseTime : baseTime.AddHours(1);
    }

    private static MeterListener StartListener(SignalLog log)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OrionLockDiagnostics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "orion.lock.leases.held_concurrent")
            {
                Interlocked.Add(ref log.Gauge, value);
            }
            log.Add(instrument.Name);
        });
        listener.SetMeasurementEventCallback<int>((instrument, _, _, _) => log.Add(instrument.Name));
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => log.Add(instrument.Name));
        listener.Start();
        return listener;
    }

    private sealed class SignalLog
    {
        private readonly List<string> names = [];

        public long Gauge;

        public IReadOnlyList<string> Names
        {
            get { lock (names) { return [.. names]; } }
        }

        public void Add(string name) { lock (names) { names.Add(name); } }

        public void Clear() { lock (names) { names.Clear(); } }
    }

    private sealed class LoggingObserver : ILockEventObserver
    {
        private readonly SignalLog log;

        public LoggingObserver(SignalLog log) => this.log = log;

        public void OnAcquired(string key, double durationMs) => log.Add("observer:acquired");

        public void OnAcquireTimedOut(string key, double waitMs) => log.Add("observer:timeout");

        public void OnLeaseLost(string key) => log.Add("observer:lost");

        public void OnReleased(string key) => log.Add("observer:released");
    }

    private static Task<bool> RenewResult(Renewal renewal) => renewal switch
    {
        Renewal.ReportsLost => Task.FromResult(false),
        Renewal.Throws => Task.FromException<bool>(new InvalidOperationException("backend unreachable")),
        _ => Task.FromResult(true),
    };

    private sealed class ScriptedExclusiveProvider : IDistributedLockProvider
    {
        private readonly Renewal renewal;

        public ScriptedExclusiveProvider(Renewal renewal) => this.renewal = renewal;

        public bool LeaseDurationIsTtl => true;

        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => RenewResult(renewal);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class ScriptedRwProvider : ISharedExclusiveLockProvider
    {
        private readonly Renewal renewal;

        public ScriptedRwProvider(Renewal renewal) => this.renewal = renewal;

        public bool LeaseDurationIsTtl => true;

        public Task<bool> TryAcquireAsync(
            string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(
            string key, string ownerToken, LockMode mode, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => RenewResult(renewal);

        public Task ReleaseAsync(string key, string ownerToken, LockMode mode, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
