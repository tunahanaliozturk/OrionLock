using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Demo;

/// <summary>
/// A single <c>DistributedLock</c> (a DI singleton) re-acquiring a key it already holds returns a
/// counted nested handle without a second backend call. Only the outermost dispose releases the
/// lease. This makes a lock-guarded method safe to call from inside another lock-guarded method on
/// the same key, without self-deadlock. Reentrancy collapses same-process re-acquisition only.
/// </summary>
internal static class ReentrancyDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Section("4. Reentrancy (same-process nested re-acquire)");

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var locker = sp.GetRequiredService<IDistributedLock>();

        DemoConsole.Step("Outer acquire of 'account:9'...");
        await using (var outer = await locker.AcquireAsync("account:9"))
        {
            DemoConsole.Result($"Outer held. IsHeld={outer.IsHeld}");

            DemoConsole.Step("Re-acquiring 'account:9' from within - returns a nested handle, no backend round-trip...");
            await using (var nested = await locker.AcquireAsync("account:9"))
            {
                DemoConsole.Result($"Nested held. IsHeld={nested.IsHeld} (reentrant counted handle).");

                // Even two levels deep the same singleton just bumps the reentrancy count.
                await using var nested2 = await locker.AcquireAsync("account:9");
                DemoConsole.Result($"Nested level 2 held. IsHeld={nested2.IsHeld}");
            }

            DemoConsole.Result("Inner handles disposed - lease NOT released yet; the outer handle still holds it.");

            // While the outer handle holds, an UNRELATED caller (different key path) is unaffected,
            // but a non-reentrant attempt on the SAME key from outside the hold would contend. We
            // demonstrate the still-held state by confirming the outer handle reports IsHeld.
            DemoConsole.Result($"Outer still reports IsHeld={outer.IsHeld} after inner disposes.");
        }

        DemoConsole.Result("Outer disposed - the outermost release returns the lease to the backend.");

        // Now the key is free, proven by a fresh single-shot acquire succeeding.
        await using var afterRelease = await locker.TryAcquireAsync("account:9");
        DemoConsole.Result(afterRelease is not null
            ? "'account:9' re-acquired after the outermost release - the lease was returned."
            : "Unexpected: 'account:9' still held after the outermost dispose.");
    }
}
