using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

/// <summary>
/// One backend backs one <see cref="IDistributedLock"/>. The library used to carry two opposite DI
/// conventions - five backends registering with <c>TryAddSingleton</c> (first wins) and three with
/// <c>RemoveAll</c> + <c>AddSingleton</c> (last wins) - so the same composition-root shape produced
/// opposite outcomes depending on which backend you happened to pick, and the quiet one ran the
/// in-memory fake in production.
/// </summary>
public sealed class OrionLockBuilderBackendTests
{
    private sealed class FakeProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public void RegisteringASecondBackend_Throws_InsteadOfSilentlyPickingOne()
    {
        var builder = new ServiceCollection().AddOrionLock().UseInMemory();

        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.UseBackend("redis", _ => new FakeProvider()));

        // Naming both is the whole point: the message has to say which one is already there.
        Assert.Contains("inmemory", ex.Message, StringComparison.Ordinal);
        Assert.Contains("redis", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInMemoryFake_NeverSurvivesAsTheProductionBackend()
    {
        // The exact reported shape: UseInMemory() then a real backend. Under TryAddSingleton the fake
        // won and nothing said so. It must now be impossible to end up resolving it by accident.
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(
            () => services.AddOrionLock().UseInMemory().UseBackend("redis", _ => new FakeProvider()));
    }

    [Fact]
    public void ReRegisteringTheSameBackend_ReplacesIt_AndLeavesExactlyOneProvider()
    {
        var services = new ServiceCollection();
        var second = new FakeProvider();

        services.AddOrionLock()
            .UseBackend("redis", _ => new FakeProvider())
            .UseBackend("redis", _ => second);

        Assert.Single(services, d => d.ServiceType == typeof(IDistributedLockProvider));
        Assert.Same(second, services.BuildServiceProvider().GetRequiredService<IDistributedLockProvider>());
    }

    [Fact]
    public void AFreshBuilder_CanOverrideTheBackend_SoATestHostStillHasAnEscapeHatch()
    {
        var services = new ServiceCollection();
        var fake = new FakeProvider();

        services.AddOrionLock().UseBackend("redis", _ => new FakeProvider());
        services.AddOrionLock().UseBackend("inmemory", _ => fake);

        Assert.Same(fake, services.BuildServiceProvider().GetRequiredService<IDistributedLockProvider>());
    }
}
