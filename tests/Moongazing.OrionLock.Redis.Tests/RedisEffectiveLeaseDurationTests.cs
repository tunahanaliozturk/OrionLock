using Moongazing.OrionLock.Providers;
using Moq;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

/// <summary>
/// <c>EffectiveLeaseDuration</c> promises the lease the backend ACTUALLY honours. Redis <c>PX</c> and
/// <c>PEXPIRE</c> take whole milliseconds and <see cref="RedisLease"/> rounds up to one, so a request
/// finer than a millisecond becomes a 1 ms TTL - while the interface default, which neither Redis
/// provider overrode, reported the sub-millisecond value straight back. The property was reporting the
/// one lease the backend does not honour.
/// </summary>
public sealed class RedisEffectiveLeaseDurationTests
{
    private static IConnectionMultiplexer Multiplexer => new Mock<IConnectionMultiplexer>().Object;

    private static RedisLockProvider Exclusive()
        => new(Multiplexer, new RedisLockOptions());

    private static RedisSharedExclusiveLockProvider ReaderWriter()
        => new(Multiplexer, new RedisSharedExclusiveLockOptions());

    public static TheoryData<TimeSpan, TimeSpan> Rounding => new()
    {
        // One tick is 100 ns: far below Redis's resolution, and it becomes a 1 ms TTL.
        { TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(1) },
        { TimeSpan.FromTicks(9999), TimeSpan.FromMilliseconds(1) },
        // 2.5 ms rounds UP, so the hold is never shorter than asked for.
        { TimeSpan.FromMilliseconds(2.5), TimeSpan.FromMilliseconds(3) },
        // A whole number of milliseconds is honoured exactly.
        { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250) },
        { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) },
    };

    [Theory]
    [MemberData(nameof(Rounding))]
    public void TheExclusiveProvider_ReportsTheLeaseRedisActuallyHonours(TimeSpan requested, TimeSpan expected)
    {
        // Through the INTERFACE, which is what the handle reads: an unoverridden provider silently falls
        // back to the default that echoes the request straight back.
        IDistributedLockProvider provider = Exclusive();

        Assert.Equal(expected, provider.EffectiveLeaseDuration(requested));
    }

    [Theory]
    [MemberData(nameof(Rounding))]
    public void TheReaderWriterProvider_ReportsItTheSameWay(TimeSpan requested, TimeSpan expected)
    {
        ISharedExclusiveLockProvider provider = ReaderWriter();

        Assert.Equal(expected, provider.EffectiveLeaseDuration(requested));
    }

    [Fact]
    public void TheReportedLeaseMatchesWhatIsWrittenToRedis()
    {
        // The two must not be able to drift: the reported value is the PX argument, expressed as a
        // TimeSpan rather than restated.
        var requested = TimeSpan.FromTicks(12_345);

        IDistributedLockProvider provider = Exclusive();

        Assert.Equal(
            RedisLease.ToMilliseconds(requested),
            (long)provider.EffectiveLeaseDuration(requested).TotalMilliseconds);
    }

    [Fact]
    public void ANonPositiveLease_IsReturnedUnchangedRatherThanThrowing()
    {
        // The core refuses a non-positive lease before any hold exists, so this is only ever a query
        // about a lease that will never be taken. A query should not throw.
        IDistributedLockProvider provider = Exclusive();

        Assert.Equal(TimeSpan.Zero, provider.EffectiveLeaseDuration(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(-1), provider.EffectiveLeaseDuration(TimeSpan.FromSeconds(-1)));
    }
}
