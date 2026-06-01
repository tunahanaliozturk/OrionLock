using System.Diagnostics;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Internal;

/// <summary>
/// Wraps an inner <see cref="IDistributedLockProvider"/> and records the duration of
/// <see cref="TryAcquireAsync"/> and <see cref="TryRenewAsync"/> on the OrionLock Meter,
/// tagged with the backend identifier resolved via <see cref="BackendNameResolver"/>.
/// </summary>
/// <remarks>
/// This decorator is applied automatically by <c>AddOrionLock</c> at <c>IDistributedLock</c>
/// construction time; backends do not need to opt in. The inner provider's exceptions and
/// return values pass through unchanged, so the measurement is observably side-effect-free.
/// </remarks>
internal sealed class MeasuringLockProvider : IDistributedLockProvider
{
    private readonly IDistributedLockProvider inner;
    private readonly string backendName;

    public MeasuringLockProvider(IDistributedLockProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
        backendName = BackendNameResolver.Resolve(inner);
    }

    /// <summary>The backend identifier used as the <c>backend</c> tag on emitted metrics.</summary>
    public string BackendName => backendName;

    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await inner.TryAcquireAsync(key, ownerToken, leaseDuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            OrionLockDiagnostics.RecordAcquireLatency(sw.Elapsed.TotalMilliseconds, backendName);
        }
    }

    public async Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await inner.TryRenewAsync(key, ownerToken, leaseDuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            OrionLockDiagnostics.RecordLeaseRenewalDuration(sw.Elapsed.TotalMilliseconds, backendName);
        }
    }

    public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
        => inner.ReleaseAsync(key, ownerToken, cancellationToken);
}
