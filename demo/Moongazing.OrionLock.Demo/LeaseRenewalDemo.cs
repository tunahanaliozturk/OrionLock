namespace Moongazing.OrionLock.Demo;

/// <summary>
/// Each acquired lock carries a lease. With <c>AutoRenew=true</c> (the default) a background
/// watchdog re-extends the lease at <c>LeaseDuration / 3</c> while the handle is alive, so the lock
/// survives well past its raw TTL. With <c>AutoRenew=false</c>, the lease is allowed to expire if
/// the holder outlives it - at which point a competing node can take the key. Two nodes share one
/// backend store so the competitor's attempt is genuine, not a reentrant re-acquire.
/// </summary>
internal static class LeaseRenewalDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Section("6. Lease auto-renewal watchdog");

        var nodes = LockNodes.Create();

        // Case A: node A holds a short 600ms lease with auto-renew ON, for ~1.5s (longer than the
        // raw TTL). The watchdog renews at ~200ms cadence, so the lease never lapses: IsHeld stays
        // true and node B cannot take the key.
        DemoConsole.Step("Node A AutoRenew=ON: 600ms lease held for ~1.5s (watchdog renews at LeaseDuration/3)...");
        await using (var renewed = await nodes.NodeA.AcquireAsync(
            "job:reconcile",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(600), AutoRenew = true }))
        {
            await Task.Delay(1500);
            DemoConsole.Result($"After 1.5s the lease is still held: IsHeld={renewed.IsHeld}, LostToken cancelled={renewed.LostToken.IsCancellationRequested}.");

            await using var competitor = await nodes.NodeB.TryAcquireAsync("job:reconcile");
            DemoConsole.Result(competitor is null
                ? "Node B's TryAcquire returned null - the renewed lease blocks them."
                : "Unexpected: node B took a renewed lease.");
        }

        DemoConsole.Result("Node A disposed - lease released normally.");

        // Case B: node A holds a short 400ms lease with auto-renew OFF. After the TTL lapses, the
        // in-memory backend treats the lease as expired and node B can acquire the same key.
        DemoConsole.Step("Node A AutoRenew=OFF: 400ms lease, then wait past its TTL so it lapses...");
        var stale = await nodes.NodeA.AcquireAsync(
            "job:expiring",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(400), AutoRenew = false });
        DemoConsole.Result($"Acquired with no watchdog. IsHeld={stale.IsHeld}");

        // Node B confirms the key is held BEFORE the lease lapses.
        await using (var tooEarly = await nodes.NodeB.TryAcquireAsync("job:expiring"))
        {
            DemoConsole.Result(tooEarly is null
                ? "Node B's immediate TryAcquire returned null - lease still valid."
                : "Unexpected: node B acquired a valid un-expired lease.");
        }

        await Task.Delay(700);
        await using var afterExpiry = await nodes.NodeB.TryAcquireAsync("job:expiring");
        DemoConsole.Result(afterExpiry is not null
            ? "Node B acquired 'job:expiring' after the un-renewed lease expired."
            : "Unexpected: lease had not lapsed yet.");

        await stale.DisposeAsync();
    }
}
