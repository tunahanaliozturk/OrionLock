using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.HealthChecks;

/// <summary>
/// Probes the registered <see cref="IDistributedLockProvider"/> by acquiring and immediately
/// releasing a sentinel lock. Surfaces backend reachability so container readiness probes can
/// fail fast when the lock backend is down.
/// </summary>
public sealed class OrionLockHealthCheck : IHealthCheck
{
    private readonly IDistributedLockProvider provider;
    private readonly OrionLockHealthCheckOptions options;

    /// <summary>Initializes the health check.</summary>
    public OrionLockHealthCheck(IDistributedLockProvider provider, OrionLockHealthCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.SentinelKey))
        {
            throw new ArgumentException(
                $"{nameof(OrionLockHealthCheckOptions)}.{nameof(OrionLockHealthCheckOptions.SentinelKey)} cannot be null or whitespace.",
                nameof(options));
        }
        this.provider = provider;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var backend = BackendNameResolver.Resolve(provider);
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["backend"] = backend,
            ["sentinel_key"] = options.SentinelKey,
        };

        var ownerToken = Guid.NewGuid().ToString("N");
        var deadline = Stopwatch.StartNew();

        try
        {
            bool acquired = false;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    acquired = await provider
                        .TryAcquireAsync(options.SentinelKey, ownerToken, options.LeaseDuration, cancellationToken)
                        .ConfigureAwait(false);

                    if (acquired || deadline.Elapsed >= options.WaitTimeout)
                    {
                        break;
                    }

                    // Short fixed back-off; the WaitTimeout default is 500 ms so we cap retries cheaply.
                    var remaining = options.WaitTimeout - deadline.Elapsed;
                    var delay = remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Probe was cancelled by the host; surface as Unhealthy without recording a result.
                throw;
            }

            if (!acquired)
            {
                // Sentinel was held by another owner for the full WaitTimeout - the backend is reachable
                // but contended. Same outcome shape as an explicit LockAcquisitionTimeoutException further down.
                OrionLockDiagnostics.RecordHealthCheckResult("degraded");
                data["elapsed_ms"] = deadline.Elapsed.TotalMilliseconds;
                return new HealthCheckResult(
                    HealthStatus.Degraded,
                    description: $"OrionLock backend '{backend}' reported contention on sentinel '{options.SentinelKey}'; could not acquire within {options.WaitTimeout}.",
                    data: data);
            }

            try
            {
                await provider.ReleaseAsync(options.SentinelKey, ownerToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Release is best-effort; the lease expires on its own. Probe outcome is still Healthy.
            }

            OrionLockDiagnostics.RecordHealthCheckResult("healthy");
            data["elapsed_ms"] = deadline.Elapsed.TotalMilliseconds;
            return new HealthCheckResult(
                HealthStatus.Healthy,
                description: $"OrionLock backend '{backend}' reachable.",
                data: data);
        }
        catch (LockAcquisitionTimeoutException ex)
        {
            OrionLockDiagnostics.RecordHealthCheckResult("degraded");
            data["error"] = ex.Message;
            return new HealthCheckResult(
                HealthStatus.Degraded,
                description: $"OrionLock backend '{backend}' reported contention on sentinel '{options.SentinelKey}'.",
                exception: ex,
                data: data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Do not record a metric for caller-driven cancellation: it is not a backend signal.
            throw;
        }
        catch (OrionLockBackendException ex)
        {
            // Honor the caller-configured failureStatus on the registration so an operator can
            // tune backend outages to Degraded (e.g., keep readiness probes from flapping on
            // transient errors) without forking this code. The metric label tracks the chosen
            // semantic.
            OrionLockDiagnostics.RecordHealthCheckResult(FailureMetricLabel(context));
            data["error"] = ex.Message;
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                description: $"OrionLock backend '{backend}' reported a backend failure.",
                exception: ex,
                data: data);
        }
        catch (Exception ex)
        {
            OrionLockDiagnostics.RecordHealthCheckResult(FailureMetricLabel(context));
            data["error"] = ex.Message;
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                description: $"OrionLock backend '{backend}' threw '{ex.GetType().Name}' during probe.",
                exception: ex,
                data: data);
        }
    }

    private static string FailureMetricLabel(HealthCheckContext context) => context.Registration.FailureStatus switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Degraded => "degraded",
        _ => "unhealthy",
    };
}
