# OrionLock.Testing

In-memory backend for testing code that uses [OrionLock](https://www.nuget.org/packages/OrionLock). No Redis or database required.

```csharp
services.AddOrionLock().UseInMemory();
```

For tests only. Trimmable and Native AOT compatible (`IsTrimmable` and `IsAotCompatible` set). See https://github.com/tunahanaliozturk/OrionLock.
