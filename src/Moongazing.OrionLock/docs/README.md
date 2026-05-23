# OrionLock

Distributed locking for .NET — a backend-agnostic `IDistributedLock` with blocking acquire, reentrancy, and background lease auto-renewal.

```csharp
services.AddOrionLock().UseRedis("localhost:6379");

await using var handle = await locker.AcquireAsync("order:42", TimeSpan.FromSeconds(30));
// critical section; handle.LostToken trips if the lease is lost
```

Backends ship separately: `OrionLock.Redis`, `OrionLock.EntityFrameworkCore`, `OrionLock.Testing`. See https://github.com/tunahanaliozturk/OrionLock for the full README.
