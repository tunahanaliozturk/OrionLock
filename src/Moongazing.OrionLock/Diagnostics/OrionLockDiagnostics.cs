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

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.1.0");

    private static readonly Meter Meter = new(MeterName, "0.1.0");

    internal static readonly Counter<long> Acquisitions = Meter.CreateCounter<long>("orionlock.acquisitions");
    internal static readonly Counter<long> Contentions = Meter.CreateCounter<long>("orionlock.contentions");
    internal static readonly Counter<long> LeasesLost = Meter.CreateCounter<long>("orionlock.lease.lost");
    internal static readonly Histogram<double> AcquireDuration = Meter.CreateHistogram<double>("orionlock.acquire.duration");
}
