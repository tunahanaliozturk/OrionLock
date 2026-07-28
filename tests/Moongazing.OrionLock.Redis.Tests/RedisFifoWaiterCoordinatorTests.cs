namespace Moongazing.OrionLock.Redis.Tests;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Fairness;
using Moongazing.OrionLock.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859",
    Justification = "Tests intentionally exercise the IFifoWaiterCoordinator surface.")]
public sealed class RedisFifoWaiterCoordinatorTests : IAsyncLifetime
{
    private readonly RedisContainer container = new RedisBuilder().Build();
#pragma warning disable CA1859 // Tests intentionally exercise the IFifoWaiterCoordinator surface.
    private IConnectionMultiplexer mux = default!;
#pragma warning restore CA1859

    public async Task InitializeAsync()
        => mux = await RedisContainerStartup.StartAndConnectAsync(container).ConfigureAwait(false);

    public async Task DisposeAsync()
    {
        await mux.DisposeAsync();
        await container.DisposeAsync();
    }

    private RedisFifoWaiterCoordinator NewCoordinator(string? prefix = null) =>
        new(mux, new RedisFifoWaiterOptions
        {
            KeyPrefix = prefix ?? "test:fifo:" + Guid.NewGuid().ToString("N"),
            PollInterval = TimeSpan.FromMilliseconds(20),
        });

    [Fact]
    public async Task First_caller_acquires_ticket_immediately()
    {
        IFifoWaiterCoordinator sut = NewCoordinator();

        var ticket = await sut.EnterAsync("k", CancellationToken.None);

        Assert.Equal("k", ticket.Key);
        await sut.LeaveAsync(ticket, CancellationToken.None);
    }

    [Fact]
    public async Task Second_caller_waits_until_first_Leaves()
    {
        IFifoWaiterCoordinator sut = NewCoordinator();

        var first = await sut.EnterAsync("k", CancellationToken.None);
        var secondTask = sut.EnterAsync("k", CancellationToken.None);

        // Second caller should be blocked while first holds the head slot. The 20ms poll loop has had
        // many cycles to (wrongly) complete within this 500ms window, so the negative assertion is a
        // genuine test of the blocking - yet the window is wide enough that a slow, loaded CI runner
        // cannot let the assertion run before the second caller has even joined the queue. The earlier
        // 80ms window flaked on net10.0 under heavy parallel container load.
        await Task.Delay(500);
        Assert.False(secondTask.IsCompleted);

        await sut.LeaveAsync(first, CancellationToken.None);

        // Generous timeout: once first leaves, the second caller becomes head on its next poll; 30s far
        // exceeds the 20ms poll interval even if the runner is badly starved, so this never spuriously
        // times out while still failing fast if the hand-off genuinely deadlocks.
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("k", second.Key);

        await sut.LeaveAsync(second, CancellationToken.None);
    }

    [Fact]
    public async Task Distinct_keys_are_independent()
    {
        IFifoWaiterCoordinator sut = NewCoordinator();

        var aTicket = await sut.EnterAsync("a", CancellationToken.None);
        var bTicket = await sut.EnterAsync("b", CancellationToken.None);

        Assert.Equal("a", aTicket.Key);
        Assert.Equal("b", bTicket.Key);

        await sut.LeaveAsync(aTicket, CancellationToken.None);
        await sut.LeaveAsync(bTicket, CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_removes_caller_from_queue()
    {
        IFifoWaiterCoordinator sut = NewCoordinator();

        var first = await sut.EnterAsync("k", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var secondTask = sut.EnterAsync("k", cts.Token);
        // Give the second caller time to actually join the queue before cancelling it; 200ms (up from
        // 50ms) tolerates a slow, loaded runner that has not yet scheduled the enqueue.
        await Task.Delay(200);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);

        // Cancelled waiter must NOT block the next caller from becoming head once `first`
        // releases the lock. If `LeaveAsync` only popped one slot and the cancelled waiter
        // still sat at position 0, this Enter would deadlock.
        await sut.LeaveAsync(first, CancellationToken.None);
        var third = await sut.EnterAsync("k", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(third);
        await sut.LeaveAsync(third, CancellationToken.None);
    }

    [Fact]
    public async Task Ticket_from_another_coordinator_throws_on_Leave()
    {
        IFifoWaiterCoordinator sut = NewCoordinator();
        var foreignTicket = new ForeignTicket("k");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.LeaveAsync(foreignTicket, CancellationToken.None));
    }

    private sealed record ForeignTicket(string Key) : IFifoWaiterTicket;

    [Fact]
    public async Task EnterAsync_records_the_live_queue_depth_each_caller_joins_behind()
    {
        var samples = new System.Collections.Generic.List<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orion.lock.fairness.queue_depth")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((_, val, _, _) =>
        {
            lock (samples) { samples.Add(val); }
        });
        listener.Start();

        IFifoWaiterCoordinator sut = NewCoordinator();

        // Head joins an empty sorted set -> depth 0 (ZRANK 0).
        var first = await sut.EnterAsync("k", CancellationToken.None);

        // Second joins behind the head -> depth 1 (ZRANK 1). The depth is recorded synchronously
        // at enter, right after the ZADD, before the returned task completes. The 500ms window (up from
        // 80ms) gives the MeterListener ample time to surface both samples even when the runner is
        // heavily loaded, without changing what is asserted.
        var secondTask = sut.EnterAsync("k", CancellationToken.None);
        await Task.Delay(500);

        lock (samples)
        {
            Assert.Contains(0, samples);
            Assert.Contains(1, samples);
        }

        await sut.LeaveAsync(first, CancellationToken.None);
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(30));
        await sut.LeaveAsync(second, CancellationToken.None);
    }

    [Fact]
    public async Task Stale_entries_older_than_WaiterTtl_are_pruned()
    {
        var coordinator = new RedisFifoWaiterCoordinator(
            mux,
            new RedisFifoWaiterOptions
            {
                KeyPrefix = "test:prune:" + Guid.NewGuid().ToString("N"),
                PollInterval = TimeSpan.FromMilliseconds(20),
                // 500ms TTL (up from 150ms) so the wall-clock wait below can clear it by a wide,
                // CI-tolerant margin rather than racing a tight 100ms cushion.
                WaiterTtl = TimeSpan.FromMilliseconds(500),
            });

        var first = await coordinator.EnterAsync("k", CancellationToken.None);

        // Wait well past the TTL (1.5s vs a 500ms TTL is 3x slack); the next EnterAsync's prune pass
        // then reliably removes the stale entry even if the runner stalled this delay. The earlier
        // 250ms-vs-150ms pairing left only 100ms of cushion, which a loaded runner could erase.
        await Task.Delay(1500);
        var second = await coordinator.EnterAsync("k", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(second);

        await coordinator.LeaveAsync(second, CancellationToken.None);
        await coordinator.LeaveAsync(first, CancellationToken.None);
    }
}
