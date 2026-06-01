using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moongazing.OrionLock.HealthChecks;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.HealthChecks.Tests;

public class OrionLockHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_FreshBackend_ReturnsHealthy()
    {
        var provider = new InMemoryLockProvider();
        var sut = new OrionLockHealthCheck(provider, new OrionLockHealthCheckOptions());

        var result = await sut.CheckHealthAsync(new HealthCheckContext { Registration = MakeRegistration() }, CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("inmemory", result.Data["backend"]);
        Assert.Equal("orionlock:_healthcheck", result.Data["sentinel_key"]);
    }

    [Fact]
    public async Task CheckHealthAsync_SentinelHeldByAnotherOwner_ReturnsDegraded()
    {
        var provider = new InMemoryLockProvider();
        var options = new OrionLockHealthCheckOptions
        {
            // Long lease so the pre-acquired sentinel cannot expire during the probe.
            LeaseDuration = TimeSpan.FromSeconds(30),
            // Short wait so the test stays fast.
            WaitTimeout = TimeSpan.FromMilliseconds(150),
        };

        // Pre-acquire the sentinel as a different owner; the probe must not be able to take it.
        var holderToken = Guid.NewGuid().ToString("N");
        Assert.True(await provider.TryAcquireAsync(
            options.SentinelKey, holderToken, TimeSpan.FromSeconds(30), CancellationToken.None));

        var sut = new OrionLockHealthCheck(provider, options);

        var result = await sut.CheckHealthAsync(new HealthCheckContext { Registration = MakeRegistration() }, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("contention", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_BackendThrows_ReturnsUnhealthy()
    {
        var provider = new ThrowingLockProvider();
        var sut = new OrionLockHealthCheck(provider, new OrionLockHealthCheckOptions());

        var result = await sut.CheckHealthAsync(new HealthCheckContext { Registration = MakeRegistration() }, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
        Assert.IsType<OrionLockBackendException>(result.Exception);
        Assert.Equal("backend exploded", ((OrionLockBackendException)result.Exception!).Reason);
        Assert.True(result.Data.ContainsKey("error"));
    }

    [Fact]
    public async Task CheckHealthAsync_GenericBackendException_ReturnsUnhealthy()
    {
        var provider = new RawExceptionLockProvider();
        var sut = new OrionLockHealthCheck(provider, new OrionLockHealthCheckOptions());

        var result = await sut.CheckHealthAsync(new HealthCheckContext { Registration = MakeRegistration() }, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    private static HealthCheckRegistration MakeRegistration()
        => new("orionlock", _ => throw new NotImplementedException(), HealthStatus.Unhealthy, tags: null);

    private sealed class ThrowingLockProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => throw new OrionLockBackendException(key, "backend exploded");

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RawExceptionLockProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => throw new InvalidOperationException("connection refused");

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
