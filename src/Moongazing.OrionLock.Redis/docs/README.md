# OrionLock.Redis

Redis backend for [OrionLock](https://www.nuget.org/packages/OrionLock). `SET NX PX` acquire with owner-checked Lua compare-and-extend / compare-and-delete.

```csharp
services.AddOrionLock().UseRedis("localhost:6379");
```

Requires the `OrionLock` package. See https://github.com/tunahanaliozturk/OrionLock.
