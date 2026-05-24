using Moongazing.OrionLock.SqlServer;

namespace Moongazing.OrionLock.SqlServer.Tests;

public partial class SqlServerLockProviderTests : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture fx;

    public SqlServerLockProviderTests(SqlServerContainerFixture fx) => this.fx = fx;

    // Validation tests do not need a real SQL Server.
    private static SqlServerLockProvider NewProviderWithoutServer(string prefix = "")
        => new("Server=does-not-matter;", new SqlServerLockOptions { KeyPrefix = prefix });

    private SqlServerLockProvider NewProvider()
        => new(fx.ConnectionString, new SqlServerLockOptions());

    // --- existing validation tests, unchanged ---

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
    public async Task TryAcquire_ShouldThrow_WhenCombinedKeyExceeds240Chars()
    {
        var p = NewProviderWithoutServer(prefix: "app:");
        var longKey = new string('x', 237); // 4 (prefix) + 237 = 241
        await Assert.ThrowsAsync<ArgumentException>(() =>
            p.TryAcquireAsync(longKey, "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldNotThrowArgumentException_AtBoundary()
    {
        var p = NewProviderWithoutServer(prefix: "app:");
        var key = new string('x', 236); // 4 + 236 = 240 (allowed)

        try
        {
            await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        }
        catch (ArgumentException)
        {
            Assert.Fail("Boundary key should not throw ArgumentException.");
        }
        catch
        {
            // SqlException or similar from the bogus connection string is fine.
        }
    }

    // --- new integration tests ---

    [Fact]
    public async Task TryAcquire_ShouldSucceedThenBlockSecondOwner()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        Assert.True(await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryAcquireAsync(key, "owner-2", TimeSpan.FromSeconds(30), default));
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

        await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        Assert.True(await p.TryRenewAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldReturnFalse_ForUnknownOwner()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        Assert.False(await p.TryRenewAsync(key, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldReturnFalse_AfterConnectionKilled()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);

        // Read the SPID of the session that holds the lock.
        var heldConn = p.GetSessionForTesting("owner-1")!;
        Assert.NotNull(heldConn);
        short spid;
        using (var cmd = heldConn.CreateCommand())
        {
            cmd.CommandText = "SELECT @@SPID";
            spid = (short)(await cmd.ExecuteScalarAsync())!;
        }

        // From a side connection, kill that session.
        await using (var killConn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await killConn.OpenAsync();
            using var kill = killConn.CreateCommand();
            kill.CommandText = $"KILL {spid}";
            await kill.ExecuteNonQueryAsync();
        }

        // Next renewal must observe the broken session and return false.
        Assert.False(await p.TryRenewAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldAllowNextAcquire_ForOwner()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync(key, "owner-1", default);

        Assert.True(await p.TryAcquireAsync(key, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldBeNoOp_ForUnknownOwnerToken()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        // Acquire as owner-1, then call Release with an unrelated token. Must not throw
        // and must not release owner-1's lock.
        await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync(key, "never-seen", default);

        Assert.False(await p.TryAcquireAsync(key, "owner-3", TimeSpan.FromSeconds(30), default));
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
