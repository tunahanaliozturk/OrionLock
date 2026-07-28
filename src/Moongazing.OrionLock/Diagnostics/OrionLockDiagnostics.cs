using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Moongazing.OrionLock.Diagnostics;

/// <summary>OrionLock OpenTelemetry instrumentation: an <see cref="ActivitySource"/> and a <see cref="Meter"/>.</summary>
public static class OrionLockDiagnostics
{
    /// <summary>The OrionLock activity source name.</summary>
    public const string ActivitySourceName = "Moongazing.OrionLock";

    /// <summary>The OrionLock meter name.</summary>
    public const string MeterName = "Moongazing.OrionLock";

    /// <summary>The tag key used to label per-backend metrics with the backend identifier (e.g. <c>redis</c>).</summary>
    public const string BackendTagName = "backend";

    /// <summary>The tag key used to label the health-check result counter (<c>healthy</c>, <c>degraded</c>, <c>unhealthy</c>).</summary>
    public const string HealthCheckResultTagName = "result";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, MeterVersion.Value);

    private static readonly Meter Meter = new(MeterName, MeterVersion.Value);

    internal static readonly Counter<long> Acquisitions = Meter.CreateCounter<long>("orion.lock.acquisitions");
    internal static readonly Counter<long> Contentions = Meter.CreateCounter<long>("orion.lock.contentions");
    internal static readonly Counter<long> LeasesLost = Meter.CreateCounter<long>("orion.lock.lease.lost");

    /// <summary>
    /// v0.3.12 static tags appended to every counter / histogram emitted by OrionLock.
    /// Set once at <c>AddOrionLock(...).WithMetricsLabel(...)</c>. Multi-tenant deployments
    /// use this to split dashboards by tenant / region without registering a separate Meter.
    /// </summary>
    /// <remarks>
    /// Mutation is single-threaded at startup; the field is exposed as a snapshot to the
    /// emission sites so a measurement does not see a half-built array. Tags configured
    /// AFTER the host starts emitting do NOT retroactively apply to previously-emitted
    /// measurements.
    /// </remarks>
    private static KeyValuePair<string, object?>[] staticTags = Array.Empty<KeyValuePair<string, object?>>();

    internal static KeyValuePair<string, object?>[] StaticTags => staticTags;

    internal static void SetStaticTags(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var snapshot = new KeyValuePair<string, object?>[tags.Count];
        var i = 0;
        foreach (var (k, v) in tags)
        {
            snapshot[i++] = new KeyValuePair<string, object?>(k, v);
        }
        staticTags = snapshot;
    }

    internal static void RecordAcquisition()
    {
        if (staticTags.Length == 0) { Acquisitions.Add(1); return; }
        Acquisitions.Add(1, staticTags);
    }

    internal static void RecordContention()
    {
        if (staticTags.Length == 0) { Contentions.Add(1); return; }
        Contentions.Add(1, staticTags);
    }

    internal static void RecordLeaseLost()
    {
        if (staticTags.Length == 0) { LeasesLost.Add(1); return; }
        LeasesLost.Add(1, staticTags);
    }

    internal static void RecordLeaseGraceExhausted()
    {
        if (staticTags.Length == 0) { LeasesGraceExhausted.Add(1); return; }
        LeasesGraceExhausted.Add(1, staticTags);
    }

    internal static void RecordAcquireDuration(double milliseconds)
    {
        if (staticTags.Length == 0) { AcquireDuration.Record(milliseconds); return; }
        AcquireDuration.Record(milliseconds, staticTags);
    }

    /// <summary>
    /// v0.3.15 distribution of how long contended acquires spent waiting. Only contended
    /// attempts (acquires that hit at least one TryAcquireAsync miss before success) emit
    /// here so the histogram tail is not diluted by zero-contention happy paths. Separate
    /// from AcquireDuration which records EVERY successful acquire.
    /// </summary>
    internal static readonly Histogram<double> ContentionDuration = Meter.CreateHistogram<double>(
        "orion.lock.contention.duration");

    internal static void RecordContentionDuration(double milliseconds)
    {
        if (staticTags.Length == 0) { ContentionDuration.Record(milliseconds); return; }
        ContentionDuration.Record(milliseconds, staticTags);
    }

    /// <summary>
    /// v0.3.16 acquire-timeout counter. Increments each time
    /// <c>DistributedLock.AcquireAsync</c> throws <c>LockAcquisitionTimeoutException</c>
    /// because the contention loop exceeded <c>WaitTimeout</c>. Distinct from
    /// <c>orion.lock.contentions</c> which counts EVERY contended TryAcquireAsync miss
    /// even when the acquire eventually succeeds; this counter only fires when the
    /// caller gave up.
    /// </summary>
    internal static readonly Counter<long> AcquireTimeouts = Meter.CreateCounter<long>(
        "orion.lock.acquire.timeout");

    internal static void RecordAcquireTimeout()
    {
        if (staticTags.Length == 0) { AcquireTimeouts.Add(1); return; }
        AcquireTimeouts.Add(1, staticTags);
    }

    /// <summary>
    /// v0.3.28 acquire-cancelled counter. Increments when <c>DistributedLock.AcquireAsync</c> is
    /// abandoned via the caller's <see cref="System.Threading.CancellationToken"/> (a graceful
    /// shutdown, or a client that gave up) rather than by exceeding <c>WaitTimeout</c>. Distinct
    /// from the v0.3.16 <c>acquire.timeout</c> counter: a timeout is a contention-SLO breach an
    /// operator may alert on, whereas a cancellation is usually expected (deployments, request
    /// aborts). Separating them keeps the timeout signal clean for alerting.
    /// </summary>
    internal static readonly Counter<long> AcquireCancellations = Meter.CreateCounter<long>(
        "orion.lock.acquire.cancelled");

    internal static void RecordAcquireCancelled()
    {
        if (staticTags.Length == 0) { AcquireCancellations.Add(1); return; }
        AcquireCancellations.Add(1, staticTags);
    }

    /// <summary>
    /// v0.3.23 cardinality-bucketed key hash for top-N hot-key analysis. Operators
    /// query <c>topk(10, sum by (key_hash)(orion_lock_acquire_timeout_total))</c> to find
    /// the buckets producing the most timeouts without exploding metric cardinality
    /// on raw key strings (a multi-tenant deployment may have millions of unique
    /// keys). Uses 64 buckets so the cardinality stays bounded regardless of input
    /// key cardinality.
    /// </summary>
    private const int KeyHashBucketCount = 64;

    /// <summary>v0.3.23: bucket a key string into one of 64 hash slots for top-N analysis.</summary>
    public static string HashKeyToBucket(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "0";
        }
        // FNV-1a over UTF-16 char bytes - cheap and deterministic across processes
        // (string.GetHashCode is randomized per AppDomain, unsuitable for metrics
        // that need cross-process bucket alignment).
        uint hash = 2166136261u;
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= 16777619u;
        }
        return ((int)(hash % KeyHashBucketCount)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>v0.3.23: record an acquire timeout tagged with the key's hash bucket.</summary>
    internal static void RecordAcquireTimeout(string key)
    {
        var bucket = HashKeyToBucket(key);
        var keyTag = new KeyValuePair<string, object?>("key_hash", bucket);
        if (staticTags.Length == 0)
        {
            AcquireTimeouts.Add(1, keyTag);
            return;
        }
        // Combined tags array; allocation is fine on the timeout path (rare event).
        var combined = new KeyValuePair<string, object?>[staticTags.Length + 1];
        System.Array.Copy(staticTags, combined, staticTags.Length);
        combined[staticTags.Length] = keyTag;
        AcquireTimeouts.Add(1, combined);
    }

    /// <summary>
    /// v0.3.17 reentrancy depth gauge: how many nested reentrant handles this process
    /// currently holds across all keys. Operators graph this alongside
    /// <c>orion.lock.leases.held_concurrent</c> to answer "are leases held with deep
    /// re-entry (nested calls re-acquiring the same key)?" - a non-zero reentrancy
    /// gauge while held_concurrent is steady reveals nested-call patterns that would
    /// otherwise look like simple long holds.
    /// </summary>
    internal static readonly UpDownCounter<long> ReentrancyDepth = Meter.CreateUpDownCounter<long>(
        "orion.lock.reentrancy.depth");

    internal static void IncrementReentrancyDepth()
    {
        if (staticTags.Length == 0) { ReentrancyDepth.Add(1); return; }
        ReentrancyDepth.Add(1, staticTags);
    }

    internal static void DecrementReentrancyDepth()
    {
        if (staticTags.Length == 0) { ReentrancyDepth.Add(-1); return; }
        ReentrancyDepth.Add(-1, staticTags);
    }

    /// <summary>
    /// v0.3.18 distribution of how long callers spent waiting for their FIFO ticket
    /// before entering the contention loop. Only fires when
    /// <see cref="DistributedLockOptions.UseFifoWaiterCoordinator"/> is enabled. Operators
    /// graph p99 to size the FIFO queue and spot a head-of-line block.
    /// </summary>
    internal static readonly Histogram<double> FifoCoordinatorEnterDuration = Meter.CreateHistogram<double>(
        "orion.lock.fairness.coordinator_enter_duration");

    internal static void RecordFifoCoordinatorEnter(double milliseconds)
    {
        if (staticTags.Length == 0) { FifoCoordinatorEnterDuration.Record(milliseconds); return; }
        FifoCoordinatorEnterDuration.Record(milliseconds, staticTags);
    }

    /// <summary>
    /// v0.3.29 distribution of how many waiters were already ahead in the FIFO queue at the moment
    /// a new candidate entered (the depth it joined behind: 0 = it became the head with no wait).
    /// Only fires when <see cref="DistributedLockOptions.UseFifoWaiterCoordinator"/> is enabled.
    /// Where the v0.3.18 <c>coordinator_enter_duration</c> measures the EFFECT (time spent waiting
    /// for the ticket), this measures the CAUSE - the concurrent contention depth. Operators graph
    /// p99 to see how deep the fair-lock queue gets under load and to size the waiter pool; a rising
    /// depth that is not matched by rising enter_duration points at fast lock turnover, while both
    /// rising together points at long hold times. The zero sample IS recorded: the fraction of
    /// uncontended (head-of-queue) entries is itself the signal. Tags inherited from v0.3.12.
    /// </summary>
    /// <remarks>
    /// Recorded by each <see cref="Fairness.IFifoWaiterCoordinator"/> implementation from inside its own
    /// EnterAsync, so the helper is public: the in-process coordinator lives in this assembly but
    /// the Redis coordinator is a separate package, and a custom third-party coordinator is equally
    /// entitled to feed the same histogram. The recorded value is LIVE waiters ahead only:
    /// coordinators must exclude already-cancelled waiters still lingering in their queue so a
    /// cancellation-heavy shutdown does not inflate the depth tail.
    /// </remarks>
    internal static readonly Histogram<int> FifoQueueDepth = Meter.CreateHistogram<int>(
        "orion.lock.fairness.queue_depth");

    /// <summary>
    /// Record the number of live waiters a candidate joined behind (0 = it became the head).
    /// Called by every <see cref="Fairness.IFifoWaiterCoordinator"/> backend from inside EnterAsync; see
    /// the <see cref="FifoQueueDepth"/> remarks for the live-waiters-only contract.
    /// </summary>
    public static void RecordFifoQueueDepth(int waitersAhead)
    {
        var value = waitersAhead < 0 ? 0 : waitersAhead;
        if (staticTags.Length == 0) { FifoQueueDepth.Record(value); return; }
        FifoQueueDepth.Record(value, staticTags);
    }

    /// <summary>
    /// v0.3.19 distribution of consecutive renewal failures observed per handle before
    /// the handle either recovers (success after N failures) or surrenders (lease lost /
    /// grace exhausted). Operators graph p99 to size <c>RenewalFailureGracePeriod</c>
    /// against actual backend flakiness rather than guessing.
    /// </summary>
    internal static readonly Histogram<int> ConsecutiveRenewalFailures = Meter.CreateHistogram<int>(
        "orion.lock.lease.renewal_failures_consecutive");

    internal static void RecordConsecutiveRenewalFailures(int count)
    {
        if (count <= 0)
        {
            return;
        }
        if (staticTags.Length == 0) { ConsecutiveRenewalFailures.Record(count); return; }
        ConsecutiveRenewalFailures.Record(count, staticTags);
    }

    /// <summary>
    /// v0.3.26 distribution of the DEEPEST reentrancy depth reached per hold lifetime
    /// (the high-water mark of nested re-acquisitions of the same key before the
    /// outermost handle is disposed). Distinct from the v0.3.17 reentrancy.depth gauge
    /// which shows the instantaneous outstanding count; this histogram's p99 reveals
    /// how deep real-world re-entry actually goes, helping operators spot accidental
    /// deep recursion that re-acquires a lock it already holds. A hold with no re-entry
    /// emits a sample of 1 so the full distribution (not just deep-nesting outliers) is
    /// visible.
    /// </summary>
    internal static readonly Histogram<int> ReentrancyMaxDepth = Meter.CreateHistogram<int>(
        "orion.lock.reentrancy.max_depth");

    internal static void RecordReentrancyMaxDepth(int maxDepth)
    {
        if (maxDepth <= 0)
        {
            return;
        }
        if (staticTags.Length == 0) { ReentrancyMaxDepth.Record(maxDepth); return; }
        ReentrancyMaxDepth.Record(maxDepth, staticTags);
    }

    /// <summary>
    /// v0.3.20 last health-check completion Unix seconds. <c>0</c> until the health
    /// check fires once. Operators query <c>(now() - last_check_at) &gt; N</c> to flag a
    /// stuck check loop separately from a backend that is actually unhealthy.
    /// </summary>
    private static long lastHealthCheckAtUnixSeconds;

    internal static readonly ObservableGauge<long> HealthCheckLastCheckAt = Meter.CreateObservableGauge<long>(
        "orion.lock.health.last_check_at_unix_seconds",
        () => Interlocked.Read(ref lastHealthCheckAtUnixSeconds),
        unit: "s",
        description: "Unix seconds at which the OrionLock health check last completed (0 = never).");

    /// <summary>
    /// Record that the health check completed at <paramref name="utcNow"/>. Atomic write
    /// keeps the gauge observer free of torn reads when racing the writer.
    /// </summary>
    public static void RecordHealthCheckCompleted(DateTime utcNow)
    {
        var stamp = new DateTimeOffset(utcNow, TimeSpan.Zero).ToUnixTimeSeconds();
        Interlocked.Exchange(ref lastHealthCheckAtUnixSeconds, stamp);
    }

    /// <summary>
    /// v0.3.21 counter: lease expired before the caller disposed the handle. Distinct
    /// from <c>orion.lock.lease.lost</c> which fires on a confirmed backend-side loss
    /// (a renewal returning false). This counter fires when the handle's lease wall
    /// clock has elapsed before <c>DisposeAsync</c> ran — typically the caller held
    /// the handle longer than the configured lease (Application-layer slow-path) or
    /// the watchdog stopped running for any reason. Operators graph the rate to spot
    /// 'holders too slow for the configured LeaseDuration' situations the lost
    /// counter alone cannot diagnose.
    /// </summary>
    internal static readonly Counter<long> LeasesExpiredBeforeRelease = Meter.CreateCounter<long>(
        "orion.lock.lease.expired_before_release");

    internal static void RecordLeaseExpiredBeforeRelease()
    {
        if (staticTags.Length == 0) { LeasesExpiredBeforeRelease.Add(1); return; }
        LeasesExpiredBeforeRelease.Add(1, staticTags);
    }

    /// <summary>
    /// v0.3.22 distribution of <c>TryAcquireAsync</c> attempts per <c>AcquireAsync</c>
    /// call. Operators graph p99 to size <c>RetryInterval</c> against actual
    /// contention shape: many attempts but quick acquire = polling too aggressively;
    /// few attempts but slow acquire = polling cadence is fine but backend / FIFO
    /// queue is slow. Emits only when the acquire eventually succeeded (cancelled or
    /// timed-out acquires do not pollute the distribution).
    /// </summary>
    internal static readonly Histogram<int> AcquireAttemptCount = Meter.CreateHistogram<int>(
        "orion.lock.acquire.attempt_count");

    internal static void RecordAcquireAttemptCount(int count)
    {
        if (count <= 0)
        {
            return;
        }
        if (staticTags.Length == 0) { AcquireAttemptCount.Record(count); return; }
        AcquireAttemptCount.Record(count, staticTags);
    }

    /// <summary>
    /// v0.3.13 gauge-like counter: how many leases this process currently holds. Operators
    /// graph this to see in real time whether a host is leaking handles, holding leases
    /// longer than expected, or whether load is concentrated on a small set of keys.
    /// </summary>
    internal static readonly UpDownCounter<long> LeasesHeldConcurrent = Meter.CreateUpDownCounter<long>(
        "orion.lock.leases.held_concurrent");

    /// <summary>
    /// v0.3.14 distribution of how long each lease was held between acquire and dispose.
    /// Pairs with the v0.3.13 held_concurrent gauge to answer "are leases held briefly or
    /// for an unusually long time?" - the gauge alone cannot distinguish steady churn from
    /// a stuck holder. Tags inherited from v0.3.12 WithMetricsLabel.
    /// </summary>
    internal static readonly Histogram<double> HandleHoldingDuration = Meter.CreateHistogram<double>(
        "orion.lock.handle.holding_duration");

    internal static void RecordHandleHoldingDuration(double milliseconds)
    {
        if (staticTags.Length == 0) { HandleHoldingDuration.Record(milliseconds); return; }
        HandleHoldingDuration.Record(milliseconds, staticTags);
    }

    /// <summary>
    /// v0.3.27 distribution of how many successful lease renewals each handle performed during
    /// its hold, recorded once at release/loss alongside the v0.3.14 holding_duration. Where
    /// holding_duration is wall-clock, this is the discrete count of background renewal
    /// round-trips a lock cost the backend: the two diverge when renewals fail (the v0.3.19
    /// failure-streak histogram) or the lease interval jitters. Operators graph p99 to find
    /// critical sections that hold a lock across many renewal cycles (lock-hold hot spots) and
    /// to size renewal load on the lock backend. The zero sample IS recorded: a short hold or an
    /// AutoRenew-off handle legitimately renews zero times, and the fraction of zero-renewal
    /// holds is itself the signal. Tags inherited from v0.3.12 WithMetricsLabel.
    /// </summary>
    internal static readonly Histogram<int> HandleRenewalsPerHold = Meter.CreateHistogram<int>(
        "orion.lock.handle.renewals_per_hold");

    internal static void RecordRenewalsPerHold(int renewals)
    {
        var value = renewals < 0 ? 0 : renewals;
        if (staticTags.Length == 0) { HandleRenewalsPerHold.Record(value); return; }
        HandleRenewalsPerHold.Record(value, staticTags);
    }

    internal static void IncrementLeasesHeld()
    {
        if (staticTags.Length == 0) { LeasesHeldConcurrent.Add(1); return; }
        LeasesHeldConcurrent.Add(1, staticTags);
    }

    internal static void DecrementLeasesHeld()
    {
        if (staticTags.Length == 0) { LeasesHeldConcurrent.Add(-1); return; }
        LeasesHeldConcurrent.Add(-1, staticTags);
    }

    /// <summary>
    /// Number of leases the v0.3.10 fairness watchdog surrendered because the
    /// <see cref="DistributedLockOptions.RenewalFailureGracePeriod"/> elapsed without a
    /// successful renewal. Distinct from <c>orion.lock.lease.lost</c>, which counts ALL
    /// confirmed losses including provider-side "lease no longer exists" returns. This
    /// counter is the operational signal that a backend was unreachable long enough to
    /// trigger the fairness deadline.
    /// </summary>
    internal static readonly Counter<long> LeasesGraceExhausted = Meter.CreateCounter<long>(
        "orion.lock.lease.grace_period_exhausted");

    /// <summary>End-to-end duration of <c>AcquireAsync</c> including wait/retry, in milliseconds.</summary>
    internal static readonly Histogram<double> AcquireDuration = Meter.CreateHistogram<double>("orion.lock.acquire.duration");

    /// <summary>
    /// Duration of a single <see cref="Providers.IDistributedLockProvider.TryAcquireAsync"/> call,
    /// in milliseconds, tagged with the backend identifier (<c>redis</c>, <c>sqlserver</c>, <c>postgres</c>,
    /// <c>efcore</c>, <c>inmemory</c>).
    /// </summary>
    internal static readonly Histogram<double> AcquireLatency = Meter.CreateHistogram<double>("orion.lock.acquire.latency");

    /// <summary>
    /// Duration of a single lease renewal call in the watchdog, in milliseconds. Useful for spotting
    /// backend slowdown that could push renewals past <c>LeaseDuration / 3</c>.
    /// </summary>
    internal static readonly Histogram<double> LeaseRenewalDuration = Meter.CreateHistogram<double>("orion.lock.lease_renewal.duration");

    /// <summary>
    /// Number of transient lease-renewal failures, tagged with <c>backend</c>. Distinct from
    /// <see cref="LeasesLost"/>: a renewal call that throws (network blip, backend timeout) before
    /// the watchdog can confirm the result is recorded here; a renewal call that successfully
    /// reports the lease is gone (peer took it, lease expired) records <see cref="LeasesLost"/>.
    /// Useful for spotting backend instability that has not yet cost real availability because
    /// the next renewal attempt succeeded.
    /// </summary>
    internal static readonly Counter<long> LeaseRenewalFailures = Meter.CreateCounter<long>("orion.lock.lease_renewal.failures");

    /// <summary>
    /// Health-check outcomes for the OrionLock health check, tagged with <c>result</c>
    /// (<c>healthy</c>, <c>degraded</c>, <c>unhealthy</c>). Incremented on every probe.
    /// </summary>
    internal static readonly Counter<long> HealthCheckResult = Meter.CreateCounter<long>("orion.lock.health_check.result");

    // Internal accessors so sibling packages (HealthChecks) can record without exposing the Meter publicly.
    internal static void RecordAcquireLatency(double milliseconds, string backend)
        => AcquireLatency.Record(milliseconds, new KeyValuePair<string, object?>(BackendTagName, backend));

    internal static void RecordLeaseRenewalDuration(double milliseconds, string backend)
        => LeaseRenewalDuration.Record(milliseconds, new KeyValuePair<string, object?>(BackendTagName, backend));

    internal static void RecordLeaseRenewalFailure(string backend)
        => LeaseRenewalFailures.Add(1, new KeyValuePair<string, object?>(BackendTagName, backend));

    internal static void RecordHealthCheckResult(string result)
        => HealthCheckResult.Add(1, new KeyValuePair<string, object?>(HealthCheckResultTagName, result));
}
