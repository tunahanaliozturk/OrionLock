using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.EntityFrameworkCore;

namespace Moongazing.OrionLock.EntityFrameworkCore.Tests;

public sealed class LockTestDbContext(DbContextOptions<LockTestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());
}

public sealed class EfCoreLockProviderTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection connection = default!;
    private IServiceProvider services = default!;

    public Task InitializeAsync()
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var sc = new ServiceCollection();
        sc.AddDbContext<LockTestDbContext>(o => o.UseSqlite(connection));
        sc.AddScoped<DbContext>(sp => sp.GetRequiredService<LockTestDbContext>());
        services = sc.BuildServiceProvider();

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<LockTestDbContext>().Database.EnsureCreated();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        connection?.Dispose();
        (services as IDisposable)?.Dispose();
    }

    private EfCoreLockProvider NewProvider()
        => new(services.GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task TryAcquire_ShouldSucceedThenBlockSecondOwner()
    {
        var p = NewProvider();
        Assert.True(await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldSucceed_AfterLeaseExpires()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromMilliseconds(50), default);
        await Task.Delay(150);
        Assert.True(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldExtendForOwner_AndRejectNonOwner()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(2), default);
        Assert.True(await p.TryRenewAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryRenewAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldOnlyReleaseForOwner()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync("k", "owner-2", default);
        Assert.False(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
        await p.ReleaseAsync("k", "owner-1", default);
        Assert.True(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers()
    {
        var p = NewProvider();
        var tasks = Enumerable.Range(0, 5)
            .Select(i => p.TryAcquireAsync("k", $"owner-{i}", TimeSpan.FromSeconds(30), default))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r));
    }
}
