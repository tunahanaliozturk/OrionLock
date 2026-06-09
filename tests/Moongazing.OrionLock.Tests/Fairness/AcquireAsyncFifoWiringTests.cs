namespace Moongazing.OrionLock.Tests.Fairness;

using Moongazing.OrionLock;
using Moongazing.OrionLock.Fairness;
using Moongazing.OrionLock.Providers;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859",
    Justification = "Tests deliberately reference public abstractions.")]
public sealed class AcquireAsyncFifoWiringTests
{
    private sealed class AlwaysFreeProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);
        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(true);
        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class CountingCoordinator : IFifoWaiterCoordinator
    {
        public int Entered { get; private set; }
        public int Left { get; private set; }

        public Task<IFifoWaiterTicket> EnterAsync(string key, CancellationToken cancellationToken)
        {
            Entered++;
            return Task.FromResult<IFifoWaiterTicket>(new Ticket(key));
        }
        public Task LeaveAsync(IFifoWaiterTicket ticket, CancellationToken cancellationToken)
        {
            Left++;
            return Task.CompletedTask;
        }
        private sealed record Ticket(string Key) : IFifoWaiterTicket;
    }

    [Fact]
    public async Task AcquireAsync_default_options_skips_coordinator()
    {
        var coord = new CountingCoordinator();
        var sut = new DistributedLock(new AlwaysFreeProvider(), coord);

        await using var handle = await sut.AcquireAsync("k", new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            AutoRenew = false,
            // UseFifoWaiterCoordinator defaults to false.
        });

        Assert.Equal(0, coord.Entered);
        Assert.Equal(0, coord.Left);
    }

    [Fact]
    public async Task AcquireAsync_with_UseFifoWaiterCoordinator_enters_and_leaves_exactly_once()
    {
        var coord = new CountingCoordinator();
        var sut = new DistributedLock(new AlwaysFreeProvider(), coord);

        await using var handle = await sut.AcquireAsync("k", new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            AutoRenew = false,
            UseFifoWaiterCoordinator = true,
        });

        Assert.Equal(1, coord.Entered);
        Assert.Equal(1, coord.Left);
    }

    [Fact]
    public async Task TryAcquireAsync_never_enters_coordinator()
    {
        var coord = new CountingCoordinator();
        var sut = new DistributedLock(new AlwaysFreeProvider(), coord);

        await using var handle = await sut.TryAcquireAsync("k", new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30),
            AutoRenew = false,
            UseFifoWaiterCoordinator = true, // ignored on TryAcquireAsync per contract
        });

        Assert.NotNull(handle);
        Assert.Equal(0, coord.Entered);
        Assert.Equal(0, coord.Left);
    }

    [Fact]
    public async Task AcquireAsync_timeout_still_calls_LeaveAsync()
    {
        var coord = new CountingCoordinator();
        // Provider that always returns false to force the polling loop to time out.
        var nonAcquiring = new NonAcquiringProvider();
        var sut = new DistributedLock(nonAcquiring, coord);

        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(() =>
            sut.AcquireAsync("k", new DistributedLockOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(30),
                WaitTimeout = TimeSpan.FromMilliseconds(50),
                RetryInterval = TimeSpan.FromMilliseconds(10),
                AutoRenew = false,
                UseFifoWaiterCoordinator = true,
            }));

        Assert.Equal(1, coord.Entered);
        Assert.Equal(1, coord.Left);
    }

    private sealed class NonAcquiringProvider : IDistributedLockProvider
    {
        public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(false);
        public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => Task.FromResult(false);
        public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
