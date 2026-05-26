namespace Moongazing.OrionLock.Postgres.Tests;

public class PostgresLockOptionsTests
{
    [Fact]
    public void Defaults_ShouldMatchSpec()
    {
        var o = new PostgresLockOptions();
        Assert.Equal(string.Empty, o.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(30), o.CommandTimeout);
    }

    [Fact]
    public void Setters_ShouldRoundTrip()
    {
        var o = new PostgresLockOptions { KeyPrefix = "app:", CommandTimeout = TimeSpan.FromSeconds(5) };
        Assert.Equal("app:", o.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(5), o.CommandTimeout);
    }
}
