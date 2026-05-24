namespace Moongazing.OrionLock.Tests;

public class OrionLockBackendExceptionTests
{
    [Fact]
    public void Constructor_ShouldCarryKeyAndInner()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new OrionLockBackendException("k1", "sp_getapplock returned -3", inner);

        Assert.Equal("k1", ex.Key);
        Assert.Same(inner, ex.InnerException);
        Assert.Contains("k1", ex.Message);
        Assert.Contains("sp_getapplock returned -3", ex.Message);
    }

    [Fact]
    public void Constructor_ShouldAllowNullInner()
    {
        var ex = new OrionLockBackendException("k2", "validation");
        Assert.Equal("k2", ex.Key);
        Assert.Null(ex.InnerException);
    }
}
