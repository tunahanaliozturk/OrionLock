using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.HealthChecks;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.HealthChecks.Tests;

public class OrionLockHealthCheckRegistrationTests
{
    private static readonly string[] ProbeTags = ["ready", "infra"];

    [Fact]
    public async Task AddOrionLockHealthCheck_UsesSuppliedName_AndRunsHealthy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionLock().UseInMemory();
        services.AddHealthChecks()
            .AddOrionLockHealthCheck(
                name: "lock-backend",
                failureStatus: HealthStatus.Degraded,
                tags: ProbeTags);

        await using var sp = services.BuildServiceProvider();
        var registrations = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        var registration = Assert.Single(registrations);
        Assert.Equal("lock-backend", registration.Name);
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
        Assert.Contains("ready", registration.Tags);
        Assert.Contains("infra", registration.Tags);

        var report = await sp.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries["lock-backend"].Status);
    }

    [Fact]
    public void AddOrionLockHealthCheck_DefaultName_IsOrionlock()
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        services.AddHealthChecks().AddOrionLockHealthCheck();

        using var sp = services.BuildServiceProvider();
        var registration = Assert.Single(sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);
        Assert.Equal("orionlock", registration.Name);
    }
}
