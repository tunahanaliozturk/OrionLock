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

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.3.20");

    private static readonly Meter Meter = new(MeterName, "0.3.20");

    internal static readonly Counter<long> Acquisitions = Meter.CreateCounter<long>("orionlock.acquisitions");
    internal static readonly Counter<long> Contentions = Meter.CreateCounter<long>("orionlock.contentions");
    internal static readonly Counter<long> LeasesLost = Meter.CreateCounter<long>("orionlock.lease.lost");

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
        "orionlock.contention.duration");

    internal static void RecordContentionDuration(double milliseconds)
    {
        if (staticTags.Length == 0) { ContentionDuration.Record(milliseconds); return; }
        ContentionDuration.Record(milliseconds, staticTags);
    }

    /// <summary>
    /// v0.3.16 acquire-timeout counter. Increments each time
    /// <c>DistributedLock.AcquireAsync</c> throws <c>LockAcquisitionTimeoutException</c>
    /// because the contention loop exceeded <c>WaitTimeout</c>. Distinct from
    /// <c>orionlock.contentions</c> which counts EVERY contended TryAcquireAsync miss
    /// even when the acquire eventually succeeds; this counter only fires when the
    /// caller gave up.
    /// </summary>
    internal static readonly Counter<long> AcquireTimeouts = Meter.CreateCounter<long>(
        "orionlock.acquire.timeout");

    internal static void RecordAcquireTimeout()
    {
        if (staticTags.Length == 0) { AcquireTimeouts.Add(1); return; }
        AcquireTimeouts.Add(1, staticTags);
    }

    /// <summary>
    /// v0.3.17 reentrancy depth gauge: how many nested reentrant handles this process
    /// currently holds across all keys. Operators graph this alongside
    /// <c>orionlock.leases.held_concurrent</c> to answer "are leases held with deep
    /// re-entry (nested calls re-acquiring the same key)?" - a non-zero reentrancy
    /// gauge while held_concurrent is steady reveals nested-call patterns that would
    /// otherwise look like simple long holds.
    /// </summary>
    internal static readonly UpDownCounter<long> ReentrancyDepth = Meter.CreateUpDownCounter<long>(
        "orionlock.reentrancy.depth");

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
        "orionlock.fairness.coordinator_enter_duration");

    internal static void RecordFifoCoordinatorEnter(double milliseconds)
    {
        if (staticTags.Length == 0) { FifoCoordinatorEnterDuration.Record(milliseconds); return; }
        FifoCoordinatorEnterDuration.Record(milliseconds, staticTags);
    }

    /// <summary>
    /// v0.3.19 distribution of consecutive renewal failures observed per handle before
    /// the handle either recovers (success after N failures) or surrenders (lease lost /
    /// grace exhausted). Operators graph p99 to size <c>RenewalFailureGracePeriod</c>
    /// against actual backend flakiness rather than guessing.
    /// </summary>
    internal static readonly Histogram<int> ConsecutiveRenewalFailures = Meter.CreateHistogram<int>(
        "orionlock.lease.renewal_failures_consecutive");

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
    /// v0.3.20 last health-check completion Unix seconds. <c>0</c> until the health
    /// check fires once. Operators query <c>(now() - last_check_at) &gt; N</c> to flag a
    /// stuck check loop separately from a backend that is actually unhealthy.
    /// </summary>
    private static long lastHealthCheckAtUnixSeconds;

    internal static readonly ObservableGauge<long> HealthCheckLastCheckAt = Meter.CreateObservableGauge<long>(
        "orionlock.health.last_check_at_unix_seconds",
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
    /// v0.3.13 gauge-like counter: how many leases this process currently holds. Operators
    /// graph this to see in real time whether a host is leaking handles, holding leases
    /// longer than expected, or whether load is concentrated on a small set of keys.
    /// </summary>
    internal static readonly UpDownCounter<long> LeasesHeldConcurrent = Meter.CreateUpDownCounter<long>(
        "orionlock.leases.held_concurrent");

    /// <summary>
    /// v0.3.14 distribution of how long each lease was held between acquire and dispose.
    /// Pairs with the v0.3.13 held_concurrent gauge to answer "are leases held briefly or
    /// for an unusually long time?" - the gauge alone cannot distinguish steady churn from
    /// a stuck holder. Tags inherited from v0.3.12 WithMetricsLabel.
    /// </summary>
    internal static readonly Histogram<double> HandleHoldingDuration = Meter.CreateHistogram<double>(
        "orionlock.handle.holding_duration");

    internal static void RecordHandleHoldingDuration(double milliseconds)
    {
        if (staticTags.Length == 0) { HandleHoldingDuration.Record(milliseconds); return; }
        HandleHoldingDuration.Record(milliseconds, staticTags);
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
    /// successful renewal. Distinct from <c>orionlock.lease.lost</c>, which counts ALL
    /// confirmed losses including provider-side "lease no longer exists" returns. This
    /// counter is the operational signal that a backend was unreachable long enough to
    /// trigger the fairness deadline.
    /// </summary>
    internal static readonly Counter<long> LeasesGraceExhausted = Meter.CreateCounter<long>(
        "orionlock.lease.grace_period_exhausted");

    /// <summary>End-to-end duration of <c>AcquireAsync</c> including wait/retry, in milliseconds.</summary>
    internal static readonly Histogram<double> AcquireDuration = Meter.CreateHistogram<double>("orionlock.acquire.duration");

    /// <summary>
    /// Duration of a single <see cref="Providers.IDistributedLockProvider.TryAcquireAsync"/> call,
    /// in milliseconds, tagged with the backend identifier (<c>redis</c>, <c>sqlserver</c>, <c>postgres</c>,
    /// <c>efcore</c>, <c>inmemory</c>).
    /// </summary>
    internal static readonly Histogram<double> AcquireLatency = Meter.CreateHistogram<double>("orionlock.acquire.latency");

    /// <summary>
    /// Duration of a single lease renewal call in the watchdog, in milliseconds. Useful for spotting
    /// backend slowdown that could push renewals past <c>LeaseDuration / 3</c>.
    /// </summary>
    internal static readonly Histogram<double> LeaseRenewalDuration = Meter.CreateHistogram<double>("orionlock.lease_renewal.duration");

    /// <summary>
    /// Number of transient lease-renewal failures, tagged with <c>backend</c>. Distinct from
    /// <see cref="LeasesLost"/>: a renewal call that throws (network blip, backend timeout) before
    /// the watchdog can confirm the result is recorded here; a renewal call that successfully
    /// reports the lease is gone (peer took it, lease expired) records <see cref="LeasesLost"/>.
    /// Useful for spotting backend instability that has not yet cost real availability because
    /// the next renewal attempt succeeded.
    /// </summary>
    internal static readonly Counter<long> LeaseRenewalFailures = Meter.CreateCounter<long>("orionlock.lease_renewal.failures");

    /// <summary>
    /// Health-check outcomes for the OrionLock health check, tagged with <c>result</c>
    /// (<c>healthy</c>, <c>degraded</c>, <c>unhealthy</c>). Incremented on every probe.
    /// </summary>
    internal static readonly Counter<long> HealthCheckResult = Meter.CreateCounter<long>("orionlock.health_check.result");

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
