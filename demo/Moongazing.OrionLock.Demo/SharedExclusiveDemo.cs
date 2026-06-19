using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Demo;

/// <summary>
/// v0.4.0 reader-writer locking via <c>ISharedExclusiveLock</c>. For a given key, any number of
/// <c>Shared</c> (read) holders coexist, OR exactly one <c>Exclusive</c> (write) holder owns it.
/// Acquire / TTL / lease / release semantics mirror the exclusive <c>IDistributedLock</c>. This
/// demo runs against the in-memory testing backend, the only backend that ships a reader-writer
/// provider in this release.
/// </summary>
internal static class SharedExclusiveDemo
{
    public static async Task RunAsync()
    {
        DemoConsole.Section("8. Shared / exclusive (reader-writer) locks");

        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();
        await using var sp = services.BuildServiceProvider();
        var rwLock = sp.GetRequiredService<ISharedExclusiveLock>();

        DemoConsole.Step("Acquiring two concurrent SHARED holds on 'catalog:42'...");
        await using (var reader1 = await rwLock.AcquireSharedAsync("catalog:42"))
        await using (var reader2 = await rwLock.AcquireSharedAsync("catalog:42"))
        {
            DemoConsole.Result($"Both shared holds active. reader1.IsHeld={reader1.IsHeld}, reader2.IsHeld={reader2.IsHeld}");

            DemoConsole.Step("Trying an EXCLUSIVE hold while readers are active (non-blocking)...");
            var blockedWriter = await rwLock.TryAcquireExclusiveAsync("catalog:42");
            DemoConsole.Result(blockedWriter is null
                ? "Exclusive try returned null - a writer cannot enter while shared holders own the key."
                : "Unexpected: exclusive try succeeded while shared holders were active.");
        }

        DemoConsole.Result("Both shared holds released.");

        DemoConsole.Step("Now acquiring an EXCLUSIVE hold on the drained key...");
        await using (var writer = await rwLock.AcquireExclusiveAsync("catalog:42"))
        {
            DemoConsole.Result($"Exclusive hold active. writer.IsHeld={writer.IsHeld}");

            DemoConsole.Step("Trying a SHARED hold while the writer is active (non-blocking)...");
            var blockedReader = await rwLock.TryAcquireSharedAsync("catalog:42");
            DemoConsole.Result(blockedReader is null
                ? "Shared try returned null - readers cannot enter while an exclusive holder owns the key."
                : "Unexpected: shared try succeeded while an exclusive holder was active.");
        }

        DemoConsole.Result("Exclusive hold released - the key is free for the next reader or writer.");
    }
}
