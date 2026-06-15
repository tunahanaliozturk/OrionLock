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
    {
        await container.StartAsync();
        mux = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
    }

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

        // Second caller should be blocked while first holds the head slot.
        await Task.Delay(80);
        Assert.False(secondTask.IsCompleted);

        await sut.LeaveAsync(first, CancellationToken.None);

        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
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
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);

        // Cancelled waiter must NOT block the next caller from becoming head once `first`
        // releases the lock. If `LeaveAsync` only popped one slot and the cancelled waiter
        // still sat at position 0, this Enter would deadlock.
        await sut.LeaveAsync(first, CancellationToken.None);
        var third = await sut.EnterAsync("k", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
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
                && instrument.Name == "orionlock.fairness.queue_depth")
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
        // at enter, right after the ZADD, before the returned task completes.
        var secondTask = sut.EnterAsync("k", CancellationToken.None);
        await Task.Delay(80);

        lock (samples)
        {
            Assert.Contains(0, samples);
            Assert.Contains(1, samples);
        }

        await sut.LeaveAsync(first, CancellationToken.None);
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
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
                WaiterTtl = TimeSpan.FromMilliseconds(150),
            });

        var first = await coordinator.EnterAsync("k", CancellationToken.None);

        // Wait past the TTL; the next EnterAsync's prune pass should remove the stale entry.
        await Task.Delay(250);
        var second = await coordinator.EnterAsync("k", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.NotNull(second);

        await coordinator.LeaveAsync(second, CancellationToken.None);
        await coordinator.LeaveAsync(first, CancellationToken.None);
    }
}
