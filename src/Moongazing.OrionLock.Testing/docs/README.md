# OrionLock.Testing

In-memory backend for testing code that uses [OrionLock](https://www.nuget.org/packages/OrionLock). No Redis or database required; the same `IDistributedLock` and `ISharedExclusiveLock` API as the distributed backends.

![OrionLock packages: the app calls the OrionLock core and one backend package implements IDistributedLockProvider underneath it](https://raw.githubusercontent.com/tunahanaliozturk/OrionLock/main/docs/diagrams/overview.png)

## Install

    dotnet add package OrionLock.Testing

It plugs into the core `OrionLock` package, which it references.

## Quick start

```csharp
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;

services.AddOrionLock().UseInMemory();

var locker = serviceProvider.GetRequiredService<IDistributedLock>();
await using var handle = await locker.AcquireAsync("order:42");

var rwLock = serviceProvider.GetRequiredService<ISharedExclusiveLock>();
await using var read = await rwLock.AcquireSharedAsync("catalog:42");
```

## What it models

- Leases expire on the clock like a TTL backend, so `AutoRenew`, `LostToken` and `MaxHoldDuration` behave as they do on Redis.
- `handle.FencingToken` is a per-key counter, strictly increasing within one process.
- A waiter parks on an in-process release signal instead of polling.
- Everything lives in one process: it proves your locking logic, not mutual exclusion across machines.

## It cannot end up in production by accident

`UseInMemory()` followed by a real backend, such as `UseInMemory().UseRedis("...")`, used to leave the
in-memory fake registered, silently, because the first registration won. Registering a second, different
backend on the same builder now throws `InvalidOperationException` naming both. To override the
production registration from a test host, start a fresh builder with another `AddOrionLock()` call.

Trimmable and Native AOT compatible (`IsTrimmable` and `IsAotCompatible` set).

## Related packages

- `OrionLock` - the core: `IDistributedLock`, options, lease watchdog.
- `OrionLock.Redis`, `OrionLock.Postgres`, `OrionLock.SqlServer`, `OrionLock.EntityFrameworkCore` - the production backends.

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionLock
- Changelog: https://github.com/tunahanaliozturk/OrionLock/blob/main/CHANGELOG.md
- License: MIT
