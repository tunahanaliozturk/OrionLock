namespace Moongazing.OrionLock.Tests.Providers;

using System.Diagnostics;
using Moongazing.OrionLock.Providers;
using Moq;
using Xunit;

public sealed class WaitForAcquireAsyncTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Returns_true_immediately_when_first_attempt_succeeds()
    {
        var mock = new Mock<IDistributedLockProvider>();
        mock.Setup(p => p.TryAcquireAsync("k", "owner", Lease, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var acquired = await mock.Object.WaitForAcquireAsync(
            "k", "owner", Lease, TimeSpan.FromSeconds(5));

        Assert.True(acquired);
        mock.Verify(p => p.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Polls_until_acquired_within_timeout()
    {
        var mock = new Mock<IDistributedLockProvider>();
        var calls = 0;
        mock.Setup(p => p.TryAcquireAsync("k", "owner", Lease, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls >= 3);

        var acquired = await mock.Object.WaitForAcquireAsync(
            "k", "owner", Lease, TimeSpan.FromSeconds(5),
            new WaitForAcquireOptions
            {
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(5),
            });

        Assert.True(acquired);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Returns_false_when_timeout_elapses_before_acquisition()
    {
        var mock = new Mock<IDistributedLockProvider>();
        mock.Setup(p => p.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var sw = Stopwatch.StartNew();
        var acquired = await mock.Object.WaitForAcquireAsync(
            "k", "owner", Lease, TimeSpan.FromMilliseconds(500),
            new WaitForAcquireOptions
            {
                InitialDelay = TimeSpan.FromMilliseconds(10),
                MaxDelay = TimeSpan.FromMilliseconds(50),
            });
        sw.Stop();

        Assert.False(acquired);
        // The helper should NOT block significantly past the requested timeout. With a 500ms timeout the
        // 5s ceiling proves it returns near the budget (not, say, blocking forever) while leaving ample
        // slack for a loaded CI runner; load only inflates the elapsed time, so a generous bound here is
        // the safe direction.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"expected < 5s, got {sw.Elapsed}");
    }

    [Fact]
    public async Task Propagates_cancellation_as_OperationCanceledException()
    {
        var mock = new Mock<IDistributedLockProvider>();
        mock.Setup(p => p.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mock.Object.WaitForAcquireAsync(
                "k", "owner", Lease, TimeSpan.FromSeconds(60),
                new WaitForAcquireOptions { InitialDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(20) },
                cts.Token));
    }

    [Fact]
    public async Task Rejects_null_or_empty_arguments()
    {
        var mock = new Mock<IDistributedLockProvider>();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DistributedLockProviderExtensions.WaitForAcquireAsync(
                null!, "k", "owner", Lease, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            mock.Object.WaitForAcquireAsync(string.Empty, "owner", Lease, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            mock.Object.WaitForAcquireAsync("k", string.Empty, Lease, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Options_validate_at_invocation_time()
    {
        var mock = new Mock<IDistributedLockProvider>();
        mock.Setup(p => p.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            mock.Object.WaitForAcquireAsync(
                "k", "owner", Lease, TimeSpan.FromSeconds(1),
                new WaitForAcquireOptions { InitialDelay = TimeSpan.Zero }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            mock.Object.WaitForAcquireAsync(
                "k", "owner", Lease, TimeSpan.FromSeconds(1),
                new WaitForAcquireOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(100),
                    MaxDelay = TimeSpan.FromMilliseconds(50),
                }));
    }

    [Fact]
    public async Task Random_factory_is_invoked_so_tests_can_pin_a_seed()
    {
        var mock = new Mock<IDistributedLockProvider>();
        mock.Setup(p => p.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var factoryCalls = 0;

        await mock.Object.WaitForAcquireAsync(
            "k", "owner", Lease, TimeSpan.FromSeconds(1),
            new WaitForAcquireOptions
            {
                RandomFactory = () => { factoryCalls++; return new Random(42); },
            });

        Assert.Equal(1, factoryCalls);
    }
}
