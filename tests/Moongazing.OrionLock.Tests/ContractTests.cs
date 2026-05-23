using Moongazing.OrionLock;

namespace Moongazing.OrionLock.Tests;

public class ContractTests
{
    [Fact]
    public void DistributedLockOptions_ShouldHaveDocumentedDefaults()
    {
        var o = new DistributedLockOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), o.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), o.WaitTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(250), o.RetryInterval);
        Assert.True(o.AutoRenew);
    }

    [Fact]
    public void LockAcquisitionTimeoutException_ShouldCarryKeyAndElapsed()
    {
        var ex = new LockAcquisitionTimeoutException("order:1", TimeSpan.FromSeconds(10));
        Assert.Equal("order:1", ex.Key);
        Assert.Equal(TimeSpan.FromSeconds(10), ex.Elapsed);
        Assert.Contains("order:1", ex.Message);
    }

    [Fact]
    public void LeaseLostException_ShouldCarryKey()
    {
        var ex = new LeaseLostException("order:1");
        Assert.Equal("order:1", ex.Key);
        Assert.Contains("order:1", ex.Message);
    }
}
