namespace Moongazing.OrionLock.HealthChecks;

/// <summary>Options for <see cref="OrionLockHealthCheck"/>.</summary>
public sealed class OrionLockHealthCheckOptions
{
    /// <summary>
    /// The key used to probe the backend. Default <c>orionlock:_healthcheck</c>.
    /// Override only if the default collides with an application key namespace.
    /// </summary>
    public string SentinelKey { get; set; } = "orionlock:_healthcheck";

    /// <summary>
    /// Lease duration for the sentinel acquisition. Default <c>2 seconds</c>.
    /// Kept short so a probe that crashes mid-acquisition expires quickly and does
    /// not block the next probe.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the probe waits for the sentinel before reporting <c>Degraded</c>.
    /// Default <c>500 ms</c>. Bounded so a slow backend reports degraded instead of
    /// holding a readiness probe open.
    /// </summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
}
