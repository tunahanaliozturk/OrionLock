using Moongazing.OrionLock.Postgres;

namespace Moongazing.OrionLock.Postgres.Tests;

public partial class PostgresLockProviderTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture fx;

    public PostgresLockProviderTests(PostgresContainerFixture fx) => this.fx = fx;

    // Validation tests do not need a real Postgres.
    private static PostgresLockProvider NewProviderWithoutServer(string prefix = "")
        => new("Host=does-not-matter;Username=u;Password=p;Database=d", new PostgresLockOptions { KeyPrefix = prefix });

    private PostgresLockProvider NewProvider()
        => new(fx.ConnectionString, new PostgresLockOptions());

    // --- unit tests ---

    [Fact]
    public void HashKey_IsDeterministic()
    {
        var a = PostgresLockProvider.HashKey("app:invoice:42");
        var b = PostgresLockProvider.HashKey("app:invoice:42");
        Assert.Equal(a, b);
    }

    [Fact]
    public void HashKey_DifferentInputsProduceDifferentHashes()
    {
        var a = PostgresLockProvider.HashKey("app:invoice:42");
        var b = PostgresLockProvider.HashKey("app:invoice:43");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task TryAcquire_ShouldThrow_WhenKeyIsEmpty()
    {
        var p = NewProviderWithoutServer();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            p.TryAcquireAsync("", "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldThrow_WhenKeyIsWhitespace()
    {
        var p = NewProviderWithoutServer();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            p.TryAcquireAsync("   ", "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public void Ctor_ShouldThrow_WhenConnectionStringIsEmpty()
    {
        Assert.Throws<ArgumentException>(() =>
            new PostgresLockProvider("", new PostgresLockOptions()));
    }

    [Fact]
    public void Ctor_ShouldThrow_WhenOptionsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PostgresLockProvider("Host=h;Username=u;Password=p;Database=d", null!));
    }

    [Fact]
    public void Ctor_ShouldThrow_WhenKeyPrefixIsNull()
    {
        var opts = new PostgresLockOptions();
        // Bypass the init-by-default of string.Empty:
        opts.KeyPrefix = null!;
        Assert.Throws<ArgumentException>(() =>
            new PostgresLockProvider("Host=h;Username=u;Password=p;Database=d", opts));
    }

    // --- integration tests ---

    [Fact]
    public async Task TryAcquire_ShouldReturnTrue_OnFirstAcquire()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        Assert.True(await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_SecondCaller_ShouldReturnFalse_WhileHeld()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        Assert.True(await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryAcquireAsync(key, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldAllowSubsequentAcquire()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        Assert.True(await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
        await p.ReleaseAsync(key, "owner-1", default);

        Assert.True(await p.TryAcquireAsync(key, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        var tasks = Enumerable.Range(0, 5)
            .Select(i => p.TryAcquireAsync(key, $"owner-{i}", TimeSpan.FromSeconds(30), default))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task TryRenew_ShouldReturnTrue_ForKnownOwner()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        Assert.True(await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.True(await p.TryRenewAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldReturnFalse_ForUnknownOwner()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        Assert.True(await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryRenewAsync(key, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldReturnFalse_WhenTokenIsValidButKeyDoesNotMatch()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";
        var otherKey = $"k-{Guid.NewGuid():N}";

        Assert.True(await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryRenewAsync(otherKey, "owner-1", TimeSpan.FromSeconds(30), default));

        // The original held session is still healthy; renew on the correct key still works.
        Assert.True(await p.TryRenewAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldBeNoOp_ForUnknownOwnerToken()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        Assert.True(await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
        await p.ReleaseAsync(key, "never-seen", default);

        Assert.False(await p.TryAcquireAsync(key, "owner-3", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldBeNoOp_WhenTokenIsValidButKeyDoesNotMatch()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";
        var otherKey = $"k-{Guid.NewGuid():N}";

        Assert.True(await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));

        // Release called with the right token but the WRONG key must not release the lock.
        await p.ReleaseAsync(otherKey, "owner-1", default);

        Assert.False(await p.TryAcquireAsync(key, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Dispose_ShouldReleaseAllOpenSessions()
    {
        var p1 = NewProvider();
        var keys = Enumerable.Range(0, 3).Select(_ => $"k-{Guid.NewGuid():N}").ToArray();

        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(await p1.TryAcquireAsync(keys[i], $"owner-{i}", TimeSpan.FromSeconds(30), default));
        }

        p1.Dispose();

        // A fresh provider must be able to acquire all three keys (the previous sessions
        // are closed, so the locks are released).
        using var p2 = NewProvider();
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(await p2.TryAcquireAsync(keys[i], $"owner-after-{i}", TimeSpan.FromSeconds(30), default));
        }
    }
}
