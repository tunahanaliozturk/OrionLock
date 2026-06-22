namespace Moongazing.OrionLock.Tests.Fairness;

using Moongazing.OrionLock.Fairness;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859",
    Justification = "Tests deliberately reference the public interface to assert it satisfies the contract.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861",
    Justification = "Test-only array literals are not in a hot path; readability trumps the micro-allocation guidance.")]
public sealed class FifoWaiterCoordinatorTests
{
    [Fact]
    public async Task NullCoordinator_completes_immediately_and_carries_key()
    {
        IFifoWaiterCoordinator c = new NullFifoWaiterCoordinator();

        var ticket = await c.EnterAsync("k", CancellationToken.None);

        Assert.Equal("k", ticket.Key);
        await c.LeaveAsync(ticket, CancellationToken.None);
    }

    [Fact]
    public async Task InProcess_head_waiter_completes_immediately()
    {
        IFifoWaiterCoordinator c = new InProcessFifoWaiterCoordinator();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ticket = await c.EnterAsync("k", CancellationToken.None);
        sw.Stop();

        // Upper bound widened to 2000ms: the head taking ~0ms vs being blocked is the property under
        // test, and 2s is still far below any real blocking yet tolerant of a loaded runner that simply
        // schedules this continuation slowly. The earlier 200ms bound could trip purely on CI load.
        Assert.True(sw.ElapsedMilliseconds < 2000, $"head should complete fast, took {sw.ElapsedMilliseconds} ms");
        await c.LeaveAsync(ticket, CancellationToken.None);
    }

    [Fact]
    public async Task InProcess_second_waiter_blocks_until_head_leaves()
    {
        IFifoWaiterCoordinator c = new InProcessFifoWaiterCoordinator();

        var head = await c.EnterAsync("k", CancellationToken.None);
        var secondTask = c.EnterAsync("k", CancellationToken.None);

        // Give the scheduler ample time to run secondTask if it could (wrongly) finish; 500ms (up from
        // 100ms) makes the negative assertion a real test of blocking while staying robust on a loaded
        // runner that schedules the waiter slowly.
        await Task.Delay(500);
        Assert.False(secondTask.IsCompleted, "second waiter should still be queued behind head");

        await c.LeaveAsync(head, CancellationToken.None);

        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("k", second.Key);

        await c.LeaveAsync(second, CancellationToken.None);
    }

    [Fact]
    public async Task InProcess_waiters_complete_in_arrival_order()
    {
        IFifoWaiterCoordinator c = new InProcessFifoWaiterCoordinator();

        var order = new List<int>();
        var first = await c.EnterAsync("k", CancellationToken.None);

        var second = c.EnterAsync("k", CancellationToken.None).ContinueWith(t => { order.Add(2); return t.Result; }, TaskScheduler.Default);
        var third = c.EnterAsync("k", CancellationToken.None).ContinueWith(t => { order.Add(3); return t.Result; }, TaskScheduler.Default);

        // Let both waiters queue behind the head before we record "1" and release. 300ms (up from 50ms)
        // ensures a loaded runner has scheduled both EnterAsync calls; ordering itself is enforced by the
        // FIFO, so this delay only guards the "2 and 3 are already waiting" precondition.
        await Task.Delay(300);
        order.Add(1);
        await c.LeaveAsync(first, CancellationToken.None);

        var s = await second;
        await c.LeaveAsync(s, CancellationToken.None);

        var t = await third;
        await c.LeaveAsync(t, CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [Fact]
    public async Task InProcess_different_keys_do_not_block_each_other()
    {
        IFifoWaiterCoordinator c = new InProcessFifoWaiterCoordinator();

        var a = await c.EnterAsync("a", CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var b = await c.EnterAsync("b", CancellationToken.None);
        sw.Stop();

        // Upper bound widened to 2000ms: a different-key head is never blocked, so the only thing that
        // grows this number is CI scheduling latency. 2s stays far below any real cross-key blocking.
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"different-key head should not block; took {sw.ElapsedMilliseconds} ms");

        await c.LeaveAsync(a, CancellationToken.None);
        await c.LeaveAsync(b, CancellationToken.None);
    }

    [Fact]
    public async Task InProcess_cancelled_non_head_waiter_does_not_block_subsequent()
    {
        IFifoWaiterCoordinator c = new InProcessFifoWaiterCoordinator();

        var head = await c.EnterAsync("k", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var secondTask = c.EnterAsync("k", cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondTask);

        // Third should now become the next head after head leaves.
        var thirdTask = c.EnterAsync("k", CancellationToken.None);
        await c.LeaveAsync(head, CancellationToken.None);

        var third = await thirdTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("k", third.Key);
        await c.LeaveAsync(third, CancellationToken.None);
    }

    [Fact]
    public async Task InProcess_LeaveAsync_rejects_foreign_ticket()
    {
        IFifoWaiterCoordinator c = new InProcessFifoWaiterCoordinator();
        IFifoWaiterCoordinator other = new NullFifoWaiterCoordinator();

        var alien = await other.EnterAsync("k", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => c.LeaveAsync(alien, CancellationToken.None));
    }
}
