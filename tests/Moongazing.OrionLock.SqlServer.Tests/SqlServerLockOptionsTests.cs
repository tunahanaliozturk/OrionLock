namespace Moongazing.OrionLock.SqlServer.Tests;

public class SqlServerLockOptionsTests
{
    [Fact]
    public void Defaults_ShouldMatchSpec()
    {
        var o = new SqlServerLockOptions();
        Assert.Equal(string.Empty, o.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(30), o.CommandTimeout);
    }

    [Fact]
    public void Setters_ShouldRoundTrip()
    {
        var o = new SqlServerLockOptions { KeyPrefix = "app:", CommandTimeout = TimeSpan.FromSeconds(5) };
        Assert.Equal("app:", o.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(5), o.CommandTimeout);
    }
}
