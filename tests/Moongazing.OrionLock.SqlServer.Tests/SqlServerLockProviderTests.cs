using Moongazing.OrionLock.SqlServer;

namespace Moongazing.OrionLock.SqlServer.Tests;

public partial class SqlServerLockProviderTests
{
    // Validation tests do not need a real SQL Server.
    private static SqlServerLockProvider NewProviderWithoutServer(string prefix = "")
        => new("Server=does-not-matter;", new SqlServerLockOptions { KeyPrefix = prefix });

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
            // SqlException or NotImplementedException from the bogus connection string / not-yet-implemented path is fine.
        }
    }
}
