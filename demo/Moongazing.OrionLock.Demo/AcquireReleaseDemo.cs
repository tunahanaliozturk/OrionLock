using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Demo;

/// <summary>
/// The acquire / release happy path: a blocking <c>AcquireAsync</c>, a critical section guarded by
/// <c>handle.LostToken</c>, and an automatic release on <c>DisposeAsync</c>.
/// </summary>
internal static class AcquireReleaseDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Section("1. Acquire and release");

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var locker = sp.GetRequiredService<IDistributedLock>();

        DemoConsole.Step("Acquiring 'order:42' with a 30s lease...");
        await using (var handle = await locker.AcquireAsync(
            "order:42",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) }))
        {
            DemoConsole.Result($"Acquired '{handle.Key}'. IsHeld={handle.IsHeld}, LostToken cancelled={handle.LostToken.IsCancellationRequested}");

            if (!handle.LostToken.IsCancellationRequested)
            {
                DemoConsole.Step("Critical section running under a held lease.");
            }
        }

        DemoConsole.Result("Handle disposed - the in-memory backend released the lease.");

        // Proof the key is free again: a fresh single-shot try-acquire now succeeds.
        await using var reacquired = await locker.TryAcquireAsync("order:42");
        DemoConsole.Result(reacquired is not null
            ? "Re-acquired 'order:42' after release - the key was free."
            : "Unexpected: 'order:42' still held after dispose.");
    }
}
