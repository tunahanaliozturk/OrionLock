# OrionLock.Testing

In-memory backend for testing code that uses [OrionLock](https://www.nuget.org/packages/OrionLock). No Redis or database required.

```csharp
services.AddOrionLock().UseInMemory();
```

**It can no longer end up in production by accident.** `UseInMemory()` followed by a real backend —
`UseInMemory().UseRedis("...")` — used to leave the in-memory fake registered, silently, because the
first registration won. Registering a second, different backend on the same builder now throws
`InvalidOperationException` naming both. To override the production registration from a test host,
start a fresh builder with another `AddOrionLock()` call.

For tests only. Trimmable and Native AOT compatible (`IsTrimmable` and `IsAotCompatible` set). See https://github.com/tunahanaliozturk/OrionLock.
