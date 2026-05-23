using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;

var services = new ServiceCollection();
services.AddOrionLock().UseInMemory();
using var sp = services.BuildServiceProvider();

var locker = sp.GetRequiredService<IDistributedLock>();

// Blocking acquire with a 30s lease and the auto-renewal watchdog.
await using (var handle = await locker.AcquireAsync("order:42",
    new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) }))
{
    Console.WriteLine($"Acquired '{handle.Key}'. IsHeld={handle.IsHeld}");

    // Reentrant re-acquire of the same key returns a nested handle, no second backend call.
    await using (var nested = await locker.AcquireAsync("order:42",
        new DistributedLockOptions { LeaseDuration = TimeSpan.FromSeconds(30) }))
    {
        Console.WriteLine($"Reentered '{nested.Key}'. IsHeld={nested.IsHeld}");
    }

    // A critical section observes LostToken so it can abort if the lease is lost.
    if (!handle.LostToken.IsCancellationRequested)
    {
        Console.WriteLine("Critical section running under a held lease.");
    }
}

// Non-blocking try-acquire.
await using var t = await locker.TryAcquireAsync("order:99");
Console.WriteLine(t is null ? "order:99 was held" : $"TryAcquire got '{t.Key}'");
