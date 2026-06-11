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

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.3.11");

    private static readonly Meter Meter = new(MeterName, "0.3.11");

    internal static readonly Counter<long> Acquisitions = Meter.CreateCounter<long>("orionlock.acquisitions");
    internal static readonly Counter<long> Contentions = Meter.CreateCounter<long>("orionlock.contentions");
    internal static readonly Counter<long> LeasesLost = Meter.CreateCounter<long>("orionlock.lease.lost");

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
