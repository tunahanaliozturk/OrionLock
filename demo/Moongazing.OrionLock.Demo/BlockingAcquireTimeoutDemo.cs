using System.Diagnostics;

namespace Moongazing.OrionLock.Demo;

/// <summary>
/// Blocking <c>AcquireAsync</c> waits up to <c>WaitTimeout</c>, polling every <c>RetryInterval</c>.
/// Two outcomes are shown across two nodes sharing one backend store: it throws
/// <see cref="LockAcquisitionTimeoutException"/> when the holder never releases, and it succeeds the
/// moment the holder lets go before the timeout elapses.
/// </summary>
internal static class BlockingAcquireTimeoutDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Section("3. Blocking acquire with WaitTimeout");

        var nodes = LockNodes.Create();

        // Case A: node A holds and never releases, so node B's blocking acquire times out.
        await using var holder = await nodes.NodeA.AcquireAsync("invoice:7");
        DemoConsole.Step("Node A owns 'invoice:7'. Node B blocks with WaitTimeout=400ms...");

        var waitOptions = new DistributedLockOptions
        {
            WaitTimeout = TimeSpan.FromMilliseconds(400),
            RetryInterval = TimeSpan.FromMilliseconds(50),
        };

        var sw = Stopwatch.StartNew();
        try
        {
            await using var _ = await nodes.NodeB.AcquireAsync("invoice:7", waitOptions);
            DemoConsole.Result("Unexpected: acquired a permanently-held lock.");
        }
        catch (LockAcquisitionTimeoutException ex)
        {
            sw.Stop();
            DemoConsole.Result($"Node B timed out after ~{sw.ElapsedMilliseconds}ms: {ex.Message}");
        }

        // Case B: node A releases 'invoice:8' mid-wait, so node B acquires before its timeout.
        DemoConsole.Step("Node A releases 'invoice:8' after 150ms while node B blocks for up to 2s...");
        var gate = await nodes.NodeA.AcquireAsync("invoice:8");

        var releaser = Task.Run(async () =>
        {
            await Task.Delay(150);
            await gate.DisposeAsync();
        });

        var sw2 = Stopwatch.StartNew();
        await using (var won = await nodes.NodeB.AcquireAsync(
            "invoice:8",
            new DistributedLockOptions { WaitTimeout = TimeSpan.FromSeconds(2), RetryInterval = TimeSpan.FromMilliseconds(25) }))
        {
            sw2.Stop();
            DemoConsole.Result($"Node B acquired '{won.Key}' after ~{sw2.ElapsedMilliseconds}ms once node A released.");
        }

        await releaser;
    }
}
