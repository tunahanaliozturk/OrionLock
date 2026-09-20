using Moongazing.OrionLock.Consul;
using Moq;

namespace Moongazing.OrionLock.Consul.Tests;

/// <summary>
/// The two things that decide whether a Consul session lock is actually safe: the lock delay that stops a
/// partitioned holder overlapping its successor, and the fact that the lock key is HTTP path data.
/// </summary>
public sealed class ConsulLockSafetyTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    public void LockDelay_DefaultsNonZero_AndStaysUnderTheDefaultWaitTimeout()
    {
        // Zero removes exactly the protection that makes a Consul session lock safe: a key freed by
        // session invalidation becomes available while the old holder has not yet noticed. It must also
        // stay under OrionLock's default 10s WaitTimeout, or a blocking waiter can never outlast it.
        var options = new ConsulLockOptions();

        Assert.True(options.LockDelay > TimeSpan.Zero);
        Assert.True(options.LockDelay < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheSessionIsCreatedWithTheConfiguredLockDelay()
    {
        var adapter = new Mock<IConsulClientAdapter>();
        adapter.Setup(a => a.CreateSessionAsync(
                It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-1");
        adapter.Setup(a => a.KvAcquireAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = new ConsulLockProvider(adapter.Object);

        Assert.True(await sut.TryAcquireAsync("k", "owner-1", Lease, CancellationToken.None));

        adapter.Verify(a => a.CreateSessionAsync(
            Lease, "release", new ConsulLockOptions().LockDelay, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("../session/destroy/abc")]
    [InlineData("orionlock/../session/destroy/abc")]
    [InlineData("orionlock/./k")]
    [InlineData("orionlock//k")]
    [InlineData("/orionlock/k")]
    [InlineData("orionlock/k/")]
    public void ATraversingKey_IsRejectedByTheCore_BeforeItCanRetargetTheRequest(string key)
    {
        // Consul.NET concatenates the key after /v1/kv/ into a UriBuilder.Path, and .NET's URI
        // canonicalisation COLLAPSES /../ - so a key like this turns a KV acquire into a call to a
        // different Consul endpoint. Encoding cannot save it (.NET unescapes %2E back to '.'). The rule
        // now lives in the core so every backend refuses the same keys; this pins that the Consul
        // traversals in particular still cannot be expressed.
        Assert.Throws<ArgumentException>(() => LockKey.Validate(key));
    }

    [Theory]
    [InlineData("orionlock/a?b", "orionlock/a%3Fb")]
    [InlineData("orionlock/a#b", "orionlock/a%23b")]
    [InlineData("orionlock/a b", "orionlock/a%20b")]
    [InlineData("orionlock/a&b=c", "orionlock/a%26b%3Dc")]
    public void UriDelimitersInAPath_ArePercentEncoded_NotSplicedIntoTheRequest(string path, string expected)
        => Assert.Equal(expected, ConsulKvPath.Encode(path));

    [Fact]
    public void ThePrefixKeepsItsHierarchy()
        => Assert.Equal("orionlock/tenant-1/orders", ConsulKvPath.Encode("orionlock/tenant-1/orders"));
}
