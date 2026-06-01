using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.HealthChecks;

/// <summary>Registers <see cref="OrionLockHealthCheck"/> on an <see cref="IHealthChecksBuilder"/>.</summary>
public static class OrionLockHealthCheckBuilderExtensions
{
    /// <summary>The default health-check registration name used when callers do not supply one.</summary>
    public const string DefaultName = "orionlock";

    /// <summary>
    /// Adds the OrionLock backend reachability health check. Requires that <c>AddOrionLock</c>
    /// (and a backend extension such as <c>UseRedis</c>) has already registered an
    /// <see cref="IDistributedLockProvider"/>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The registration name. Defaults to <c>orionlock</c>.</param>
    /// <param name="failureStatus">
    /// Status reported when the check throws or the provider is missing. Defaults to <see cref="HealthStatus.Unhealthy"/>.
    /// Pass <see cref="HealthStatus.Degraded"/> to keep readiness probes from flapping on transient backend errors.
    /// </param>
    /// <param name="tags">Optional tags used to filter health-check runs.</param>
    /// <param name="configure">Optional configuration callback for <see cref="OrionLockHealthCheckOptions"/>.</param>
    /// <param name="timeout">Optional probe timeout enforced by the health-check infrastructure.</param>
    public static IHealthChecksBuilder AddOrionLockHealthCheck(
        this IHealthChecksBuilder builder,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        Action<OrionLockHealthCheckOptions>? configure = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var registrationName = string.IsNullOrWhiteSpace(name) ? DefaultName : name;

        return builder.Add(new HealthCheckRegistration(
            registrationName,
            sp =>
            {
                var options = new OrionLockHealthCheckOptions();
                configure?.Invoke(options);
                var provider = sp.GetRequiredService<IDistributedLockProvider>();
                return new OrionLockHealthCheck(provider, options);
            },
            failureStatus,
            tags,
            timeout));
    }
}
