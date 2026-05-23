# OrionLock v0.1.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build OrionLock — a standalone .NET distributed-locking library: a backend-agnostic `IDistributedLock` with Redis and EF Core lock-table backends, plus blocking acquire with retry, reentrancy, and a background lease-renewal watchdog that makes lease loss observable.

**Architecture:** Four NuGet packages. `OrionLock` (core) owns the abstraction and the backend-agnostic value-adds — it composes a small `IDistributedLockProvider` primitive into a full `IDistributedLock` with retry, reentrancy, and a renewal watchdog. `OrionLock.Redis` and `OrionLock.EntityFrameworkCore` implement only the primitive. `OrionLock.Testing` ships an in-memory provider for tests.

**Tech Stack:** .NET 8/9/10, C# latest, xUnit, `StackExchange.Redis`, EF Core (SQLite for tests), `Testcontainers` for Redis tests, BenchmarkDotNet.

**Spec:** `docs/superpowers/specs/2026-05-21-orionlock-design.md`

**Repository:** `Desktop/OrionLock/`, fresh git repo, branch `main`. The design spec is already committed (`456f846`).

---

## Conventions (apply to every task)

- **No `Co-Authored-By` trailer in commit messages. No emojis.**
- The git repo has no configured user; commit with `git -c user.name="Tunahan Ali Ozturk" -c user.email="ozturktunahanali@gmail.com" commit ...`.
- Test framework: xUnit, `[Fact]`/`[Theory]`, plain `Assert.X`. Naming `MethodUnderTest_ShouldDoX_WhenY`.
- Packable libraries multi-target `net8.0;net9.0;net10.0`; test/bench/sample projects are `IsPackable=false` and pin a single `net10.0` **in their own csproj body** (`<TargetFramework>net10.0</TargetFramework>` + `<TargetFrameworks></TargetFrameworks>`).
- `TreatWarningsAsErrors=true` — code must be warning-clean.
- Commit after every task with the message in the task's final step.
- Verification: `dotnet build` clean and `dotnet test` green before each commit.
- Namespaces: core `Moongazing.OrionLock` (+ `.Providers`); Redis `Moongazing.OrionLock.Redis`; EF Core `Moongazing.OrionLock.EntityFrameworkCore`; testing `Moongazing.OrionLock.Testing`.

## File Structure

### `src/Moongazing.OrionLock` -> package `OrionLock`

| Path | Responsibility |
|---|---|
| `IDistributedLock.cs` | `IDistributedLock` (`AcquireAsync`, `TryAcquireAsync`) |
| `IDistributedLockHandle.cs` | `IDistributedLockHandle` (`Key`, `IsHeld`, `LostToken`) |
| `DistributedLockOptions.cs` | options record |
| `LockExceptions.cs` | `LockAcquisitionTimeoutException`, `LeaseLostException` |
| `Providers/IDistributedLockProvider.cs` | the raw `TryAcquire`/`TryRenew`/`Release` primitive |
| `Internal/DistributedLockHandle.cs` | handle + renewal watchdog |
| `Internal/ReentrantLockHandle.cs` | nested counted handle |
| `Internal/ReentrancyRegistry.cs` | process-local `(key,owner)` registry |
| `DistributedLock.cs` | `IDistributedLock` over a provider — retry, reentrancy, handle creation |
| `Diagnostics/OrionLockDiagnostics.cs` | `ActivitySource` + `Meter` |
| `DependencyInjection/OrionLockBuilder.cs` | builder returned by `AddOrionLock` |
| `DependencyInjection/ServiceCollectionExtensions.cs` | `AddOrionLock` |

### `src/Moongazing.OrionLock.Redis` -> package `OrionLock.Redis`

| Path | Responsibility |
|---|---|
| `RedisLockProvider.cs` | `IDistributedLockProvider` over `StackExchange.Redis` |
| `RedisLockOptions.cs` | key prefix, connection |
| `OrionLockRedisBuilderExtensions.cs` | `UseRedis(...)` on `OrionLockBuilder` |

### `src/Moongazing.OrionLock.EntityFrameworkCore` -> package `OrionLock.EntityFrameworkCore`

| Path | Responsibility |
|---|---|
| `OrionLockRow.cs` | EF entity for `OrionLock_Locks` |
| `OrionLockRowEntityTypeConfiguration.cs` | EF mapping |
| `EfCoreLockProvider.cs` | `IDistributedLockProvider` over a `DbContext` |
| `OrionLockEfCoreBuilderExtensions.cs` | `UseEntityFrameworkCore<TDbContext>()` |

### `src/Moongazing.OrionLock.Testing` -> package `OrionLock.Testing`

| Path | Responsibility |
|---|---|
| `InMemoryLockProvider.cs` | in-process `IDistributedLockProvider` with real lease expiry |
| `OrionLockTestingBuilderExtensions.cs` | `UseInMemory()` on `OrionLockBuilder` |

### tests / bench / sample

`tests/Moongazing.OrionLock.Tests`, `tests/Moongazing.OrionLock.Redis.Tests`, `tests/Moongazing.OrionLock.EntityFrameworkCore.Tests`, `tests/Moongazing.OrionLock.Testing.Tests`, `bench/Moongazing.OrionLock.Benchmarks`, `sample/Moongazing.OrionLock.Sample`.

---

## Task 1: Scaffold the solution and projects

**Files:** the whole skeleton.

- [ ] **Step 1: Create `Directory.Build.props` at repo root**

```xml
<Project>
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>CS1591;NU1900;NU1901;NU1902;NU1903;NU1904</NoWarn>
    <Authors>Tunahan Ali Ozturk</Authors>
    <Company>Tunahan Ali Ozturk</Company>
    <RepositoryUrl>https://github.com/tunahanaliozturk/OrionLock</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageProjectUrl>https://github.com/tunahanaliozturk/OrionLock</PackageProjectUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>
```

Note: unlike a conditioned `IsPackable` block (which is dead code — `Directory.Build.props` imports before the project body sets `IsPackable`), packable metadata is applied unconditionally here. Non-packable projects pin their TFM in their own body (Step 5).

- [ ] **Step 2: Create the four `src` library csproj files**

`src/Moongazing.OrionLock/Moongazing.OrionLock.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>OrionLock</PackageId>
    <Description>Distributed locking for .NET. A backend-agnostic IDistributedLock with blocking acquire, reentrancy, and background lease auto-renewal. Backends ship separately (OrionLock.Redis, OrionLock.EntityFrameworkCore).</Description>
    <PackageTags>distributed-lock;redis;redlock;ef-core;locking;concurrency</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
  </ItemGroup>
</Project>
```

`src/Moongazing.OrionLock.Redis/Moongazing.OrionLock.Redis.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>OrionLock.Redis</PackageId>
    <Description>Redis backend for OrionLock distributed locking.</Description>
    <PackageTags>distributed-lock;redis;redlock;orionlock</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="StackExchange.Redis" Version="2.8.16" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Moongazing.OrionLock\Moongazing.OrionLock.csproj" />
  </ItemGroup>
</Project>
```

`src/Moongazing.OrionLock.EntityFrameworkCore/Moongazing.OrionLock.EntityFrameworkCore.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>OrionLock.EntityFrameworkCore</PackageId>
    <Description>EF Core lock-table backend for OrionLock distributed locking.</Description>
    <PackageTags>distributed-lock;ef-core;locking;orionlock</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Moongazing.OrionLock\Moongazing.OrionLock.csproj" />
  </ItemGroup>
</Project>
```

`src/Moongazing.OrionLock.Testing/Moongazing.OrionLock.Testing.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>OrionLock.Testing</PackageId>
    <Description>In-memory backend for testing code that uses OrionLock distributed locking.</Description>
    <PackageTags>distributed-lock;testing;orionlock</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Moongazing.OrionLock\Moongazing.OrionLock.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create the four test csproj files**

Each test project (`tests/Moongazing.OrionLock.Tests`, `.Redis.Tests`, `.EntityFrameworkCore.Tests`, `.Testing.Tests`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <TargetFramework>net10.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="coverlet.collector" Version="6.0.2" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```

ProjectReferences per test project:
- `.Tests` -> `Moongazing.OrionLock` and `Moongazing.OrionLock.Testing`
- `.Redis.Tests` -> `Moongazing.OrionLock` and `Moongazing.OrionLock.Redis`; plus `<PackageReference Include="Testcontainers.Redis" Version="3.10.0" />`
- `.EntityFrameworkCore.Tests` -> `Moongazing.OrionLock` and `Moongazing.OrionLock.EntityFrameworkCore`; plus `<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0" />`
- `.Testing.Tests` -> `Moongazing.OrionLock` and `Moongazing.OrionLock.Testing`

- [ ] **Step 4: Create the bench and sample csproj files**

`bench/Moongazing.OrionLock.Benchmarks/Moongazing.OrionLock.Benchmarks.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <TargetFramework>net10.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.14.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Moongazing.OrionLock\Moongazing.OrionLock.csproj" />
    <ProjectReference Include="..\..\src\Moongazing.OrionLock.Testing\Moongazing.OrionLock.Testing.csproj" />
  </ItemGroup>
</Project>
```

`sample/Moongazing.OrionLock.Sample/Moongazing.OrionLock.Sample.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <TargetFramework>net10.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Moongazing.OrionLock\Moongazing.OrionLock.csproj" />
    <ProjectReference Include="..\..\src\Moongazing.OrionLock.Testing\Moongazing.OrionLock.Testing.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create the solution (classic `.sln`) and add every project**

```
dotnet new sln -n Moongazing.OrionLock --format sln
dotnet sln add src/Moongazing.OrionLock src/Moongazing.OrionLock.Redis src/Moongazing.OrionLock.EntityFrameworkCore src/Moongazing.OrionLock.Testing
dotnet sln add tests/Moongazing.OrionLock.Tests tests/Moongazing.OrionLock.Redis.Tests tests/Moongazing.OrionLock.EntityFrameworkCore.Tests tests/Moongazing.OrionLock.Testing.Tests
dotnet sln add bench/Moongazing.OrionLock.Benchmarks sample/Moongazing.OrionLock.Sample
```

`--format sln` forces the classic format (the .NET 10 SDK defaults to `.slnx`, which the .NET 8 SDK CI matrix leg cannot parse).

- [ ] **Step 6: Add placeholders so every project compiles**

Each `src` project needs one `.cs` file — create `_Placeholder.cs` with just `namespace <project namespace>;` in each of the four src projects.

Each test project needs one trivial test — `SmokeTest.cs`:

```csharp
namespace Moongazing.OrionLock.Tests;   // adjust namespace per project

public class SmokeTest
{
    [Fact]
    public void SolutionBuilds() => Assert.True(true);
}
```

Bench and sample need a `Program.cs` — a one-line `System.Console.WriteLine("OrionLock");` each.

- [ ] **Step 7: Build and test**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds 0 warnings; 4 smoke tests pass.

- [ ] **Step 8: Commit**

```
git add -A
git -c user.name="Tunahan Ali Ozturk" -c user.email="ozturktunahanali@gmail.com" commit -m "chore(orionlock): scaffold solution, projects, Directory.Build.props"
```

---

## Task 2: Core contracts — interfaces, options, exceptions

**Files:**
- Create: `src/Moongazing.OrionLock/IDistributedLock.cs`
- Create: `src/Moongazing.OrionLock/IDistributedLockHandle.cs`
- Create: `src/Moongazing.OrionLock/DistributedLockOptions.cs`
- Create: `src/Moongazing.OrionLock/LockExceptions.cs`
- Create: `src/Moongazing.OrionLock/Providers/IDistributedLockProvider.cs`
- Test: `tests/Moongazing.OrionLock.Tests/ContractTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Moongazing.OrionLock.Tests/ContractTests.cs`:

```csharp
using Moongazing.OrionLock;

namespace Moongazing.OrionLock.Tests;

public class ContractTests
{
    [Fact]
    public void DistributedLockOptions_ShouldHaveDocumentedDefaults()
    {
        var o = new DistributedLockOptions();
        Assert.Equal(TimeSpan.FromSeconds(30), o.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), o.WaitTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(250), o.RetryInterval);
        Assert.True(o.AutoRenew);
    }

    [Fact]
    public void LockAcquisitionTimeoutException_ShouldCarryKeyAndElapsed()
    {
        var ex = new LockAcquisitionTimeoutException("order:1", TimeSpan.FromSeconds(10));
        Assert.Equal("order:1", ex.Key);
        Assert.Equal(TimeSpan.FromSeconds(10), ex.Elapsed);
        Assert.Contains("order:1", ex.Message);
    }

    [Fact]
    public void LeaseLostException_ShouldCarryKey()
    {
        var ex = new LeaseLostException("order:1");
        Assert.Equal("order:1", ex.Key);
        Assert.Contains("order:1", ex.Message);
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test tests/Moongazing.OrionLock.Tests --filter ContractTests`
Expected: build error — types do not exist.

- [ ] **Step 3: Create `IDistributedLock.cs`**

```csharp
namespace Moongazing.OrionLock;

/// <summary>Acquires named distributed locks across processes and machines.</summary>
public interface IDistributedLock
{
    /// <summary>
    /// Acquires the lock for <paramref name="key"/>, waiting up to <see cref="DistributedLockOptions.WaitTimeout"/>.
    /// </summary>
    /// <exception cref="LockAcquisitionTimeoutException">The lock could not be acquired before the wait timeout.</exception>
    Task<IDistributedLockHandle> AcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Tries once, without waiting, to acquire the lock. Returns <see langword="null"/> if it is held.</summary>
    Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Create `IDistributedLockHandle.cs`**

```csharp
namespace Moongazing.OrionLock;

/// <summary>
/// A held distributed lock. Dispose to release. While alive, a background watchdog renews the
/// lease (when <see cref="DistributedLockOptions.AutoRenew"/> is set); if renewal fails,
/// <see cref="IsHeld"/> becomes false and <see cref="LostToken"/> is cancelled.
/// </summary>
public interface IDistributedLockHandle : IAsyncDisposable
{
    /// <summary>The lock key this handle holds.</summary>
    string Key { get; }

    /// <summary>True while the lease is held; false once released or lost.</summary>
    bool IsHeld { get; }

    /// <summary>Cancelled if the lease is lost while the handle is alive.</summary>
    CancellationToken LostToken { get; }
}
```

- [ ] **Step 5: Create `DistributedLockOptions.cs`**

```csharp
namespace Moongazing.OrionLock;

/// <summary>Per-acquisition options for <see cref="IDistributedLock"/>.</summary>
public sealed class DistributedLockOptions
{
    /// <summary>How long the lease is valid before it expires. Default 30 seconds.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a blocking <see cref="IDistributedLock.AcquireAsync"/> waits. Default 10 seconds.</summary>
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Delay between acquisition attempts inside a blocking acquire. Default 250 ms.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>When true, a background watchdog re-extends the lease while the handle is alive. Default true.</summary>
    public bool AutoRenew { get; set; } = true;
}
```

- [ ] **Step 6: Create `LockExceptions.cs`**

```csharp
namespace Moongazing.OrionLock;

/// <summary>Thrown when a blocking acquire cannot obtain the lock before the wait timeout.</summary>
public sealed class LockAcquisitionTimeoutException : Exception
{
    /// <summary>Initializes the exception.</summary>
    public LockAcquisitionTimeoutException(string key, TimeSpan elapsed)
        : base($"Could not acquire distributed lock '{key}' within {elapsed}.")
    {
        Key = key;
        Elapsed = elapsed;
    }

    /// <summary>The lock key.</summary>
    public string Key { get; }

    /// <summary>How long the acquire waited before giving up.</summary>
    public TimeSpan Elapsed { get; }
}

/// <summary>Thrown when an operation requires a held lease that is no longer owned.</summary>
public sealed class LeaseLostException : Exception
{
    /// <summary>Initializes the exception.</summary>
    public LeaseLostException(string key)
        : base($"The lease for distributed lock '{key}' was lost.")
    {
        Key = key;
    }

    /// <summary>The lock key.</summary>
    public string Key { get; }
}
```

- [ ] **Step 7: Create `Providers/IDistributedLockProvider.cs`**

```csharp
namespace Moongazing.OrionLock.Providers;

/// <summary>
/// The raw, single-attempt lock primitive a backend implements. The core OrionLock package
/// composes reentrancy, lease renewal, and blocking-acquire retry on top of this.
/// </summary>
public interface IDistributedLockProvider
{
    /// <summary>Tries once, without waiting, to acquire <paramref name="key"/> for <paramref name="ownerToken"/>.</summary>
    Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Extends the lease if and only if <paramref name="ownerToken"/> still owns it.</summary>
    Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Releases the lock if and only if <paramref name="ownerToken"/> still owns it.</summary>
    Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken);
}
```

- [ ] **Step 8: Delete the core placeholder, run tests, expect PASS**

Delete `src/Moongazing.OrionLock/_Placeholder.cs`.
Run: `dotnet test tests/Moongazing.OrionLock.Tests --filter ContractTests`
Expected: 3 tests pass.

- [ ] **Step 9: Commit**

```
git add src/Moongazing.OrionLock tests/Moongazing.OrionLock.Tests/ContractTests.cs
git commit -m "feat(orionlock): core contracts - IDistributedLock, handle, options, exceptions, provider"
```

---

## Task 3: `InMemoryLockProvider` (testing backend)

**Files:**
- Create: `src/Moongazing.OrionLock.Testing/InMemoryLockProvider.cs`
- Delete: `src/Moongazing.OrionLock.Testing/_Placeholder.cs`
- Test: `tests/Moongazing.OrionLock.Testing.Tests/InMemoryLockProviderTests.cs`

> This provider exists early because every core composition test (Tasks 4-7) runs against it.

- [ ] **Step 1: Write the failing tests**

`tests/Moongazing.OrionLock.Testing.Tests/InMemoryLockProviderTests.cs`:

```csharp
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Testing.Tests;

public class InMemoryLockProviderTests
{
    [Fact]
    public async Task TryAcquire_ShouldSucceed_WhenKeyFree()
    {
        var p = new InMemoryLockProvider();
        Assert.True(await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldFail_WhenKeyHeldByAnother()
    {
        var p = new InMemoryLockProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default);
        Assert.False(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldSucceed_AfterLeaseExpires()
    {
        var p = new InMemoryLockProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromMilliseconds(50), default);
        await Task.Delay(120);
        Assert.True(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldFail_WhenNotOwner()
    {
        var p = new InMemoryLockProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default);
        Assert.False(await p.TryRenewAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
        Assert.True(await p.TryRenewAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldOnlyReleaseForOwner()
    {
        var p = new InMemoryLockProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync("k", "owner-2", default);   // wrong owner - no-op
        Assert.False(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
        await p.ReleaseAsync("k", "owner-1", default);   // real owner
        Assert.True(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test tests/Moongazing.OrionLock.Testing.Tests --filter InMemoryLockProviderTests`
Expected: build error.

- [ ] **Step 3: Create `InMemoryLockProvider.cs`**

```csharp
using System.Collections.Concurrent;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Testing;

/// <summary>
/// In-process <see cref="IDistributedLockProvider"/> with real lease-expiry semantics, for unit
/// tests that should not depend on a Redis server or a database.
/// </summary>
public sealed class InMemoryLockProvider : IDistributedLockProvider
{
    private sealed record Lease(string OwnerToken, DateTime ExpiresOnUtc);

    private readonly ConcurrentDictionary<string, Lease> leases = new();

    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var fresh = new Lease(ownerToken, now + leaseDuration);

        while (true)
        {
            if (leases.TryGetValue(key, out var existing))
            {
                if (existing.ExpiresOnUtc > now)
                {
                    return Task.FromResult(false);
                }
                if (leases.TryUpdate(key, fresh, existing))
                {
                    return Task.FromResult(true);
                }
                continue; // raced - retry
            }
            if (leases.TryAdd(key, fresh))
            {
                return Task.FromResult(true);
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        if (leases.TryGetValue(key, out var existing)
            && existing.OwnerToken == ownerToken
            && existing.ExpiresOnUtc > DateTime.UtcNow)
        {
            var renewed = existing with { ExpiresOnUtc = DateTime.UtcNow + leaseDuration };
            return Task.FromResult(leases.TryUpdate(key, renewed, existing));
        }
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        if (leases.TryGetValue(key, out var existing) && existing.OwnerToken == ownerToken)
        {
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, Lease>>)leases)
                .Remove(new System.Collections.Generic.KeyValuePair<string, Lease>(key, existing));
        }
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Delete the testing placeholder, run tests, expect PASS**

Delete `src/Moongazing.OrionLock.Testing/_Placeholder.cs`.
Run: `dotnet test tests/Moongazing.OrionLock.Testing.Tests --filter InMemoryLockProviderTests`
Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```
git add src/Moongazing.OrionLock.Testing tests/Moongazing.OrionLock.Testing.Tests/InMemoryLockProviderTests.cs
git commit -m "feat(orionlock): InMemoryLockProvider for tests"
```

---

## Task 4: `DistributedLockHandle` and the renewal watchdog

**Files:**
- Create: `src/Moongazing.OrionLock/Internal/DistributedLockHandle.cs`
- Test: `tests/Moongazing.OrionLock.Tests/DistributedLockHandleTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Moongazing.OrionLock.Tests/DistributedLockHandleTests.cs`:

```csharp
using Moongazing.OrionLock;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Tests;

public class DistributedLockHandleTests
{
    // A provider whose renew result is switchable, and which records release calls.
    private sealed class FakeProvider : IDistributedLockProvider
    {
        public volatile bool RenewSucceeds = true;
        public int RenewCount;
        public int ReleaseCount;

        public Task<bool> TryAcquireAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(true);

        public Task<bool> TryRenewAsync(string k, string o, TimeSpan d, CancellationToken c)
        {
            Interlocked.Increment(ref RenewCount);
            return Task.FromResult(RenewSucceeds);
        }

        public Task ReleaseAsync(string k, string o, CancellationToken c)
        {
            Interlocked.Increment(ref ReleaseCount);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Handle_ShouldExposeKeyAndBeHeld_OnCreation()
    {
        var p = new FakeProvider();
        await using var h = new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { AutoRenew = false });
        Assert.Equal("k", h.Key);
        Assert.True(h.IsHeld);
        Assert.False(h.LostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Watchdog_ShouldRenew_WhileLeaseHeld()
    {
        var p = new FakeProvider();
        await using (new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(150), AutoRenew = true }))
        {
            await Task.Delay(400);   // ~7 renewal intervals of 50 ms
        }
        Assert.True(p.RenewCount >= 2);
    }

    [Fact]
    public async Task Watchdog_ShouldFlipIsHeldAndTripLostToken_WhenRenewalFails()
    {
        var p = new FakeProvider { RenewSucceeds = false };
        await using var h = new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(150), AutoRenew = true });

        var lost = new TaskCompletionSource();
        h.LostToken.Register(() => lost.TrySetResult());
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(h.IsHeld);
        Assert.True(h.LostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_ShouldReleaseAndStopWatchdog()
    {
        var p = new FakeProvider();
        var h = new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { LeaseDuration = TimeSpan.FromMilliseconds(150), AutoRenew = true });
        await h.DisposeAsync();

        Assert.Equal(1, p.ReleaseCount);
        Assert.False(h.IsHeld);
        var renewsAfterDispose = p.RenewCount;
        await Task.Delay(200);
        Assert.Equal(renewsAfterDispose, p.RenewCount);   // watchdog stopped
    }

    [Fact]
    public async Task Dispose_ShouldBeIdempotent()
    {
        var p = new FakeProvider();
        var h = new DistributedLockHandle(p, "k", "owner-1",
            new DistributedLockOptions { AutoRenew = false });
        await h.DisposeAsync();
        await h.DisposeAsync();
        Assert.Equal(1, p.ReleaseCount);
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test tests/Moongazing.OrionLock.Tests --filter DistributedLockHandleTests`
Expected: build error.

- [ ] **Step 3: Create `Internal/DistributedLockHandle.cs`**

```csharp
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Internal;

/// <summary>
/// The concrete lock handle. Runs a background watchdog that renews the lease at
/// <c>LeaseDuration / 3</c> intervals; on renewal failure it flips <see cref="IsHeld"/> and
/// trips <see cref="LostToken"/>. Disposing stops the watchdog and releases the lock.
/// </summary>
public sealed class DistributedLockHandle : IDistributedLockHandle
{
    private readonly IDistributedLockProvider provider;
    private readonly string ownerToken;
    private readonly TimeSpan leaseDuration;
    private readonly CancellationTokenSource lostСts = new();
    private readonly CancellationTokenSource? watchdogCts;
    private readonly Task? watchdog;
    private int disposed;
    private volatile bool isHeld = true;

    /// <summary>Creates a handle and, when <see cref="DistributedLockOptions.AutoRenew"/> is set, starts the watchdog.</summary>
    public DistributedLockHandle(
        IDistributedLockProvider provider, string key, string ownerToken, DistributedLockOptions options)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        this.provider = provider;
        Key = key;
        this.ownerToken = ownerToken;
        leaseDuration = options.LeaseDuration;

        if (options.AutoRenew)
        {
            watchdogCts = new CancellationTokenSource();
            watchdog = RenewLoopAsync(watchdogCts.Token);
        }
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public bool IsHeld => isHeld;

    /// <inheritdoc />
    public CancellationToken LostToken => lostСts.Token;

    private async Task RenewLoopAsync(CancellationToken ct)
    {
        // Renew at one third of the lease so a single transient failure does not lose the lease.
        var interval = TimeSpan.FromTicks(Math.Max(leaseDuration.Ticks / 3, TimeSpan.FromMilliseconds(10).Ticks));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);

                bool renewed;
                try
                {
                    renewed = await provider.TryRenewAsync(Key, ownerToken, leaseDuration, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    renewed = false;
                }

                if (!renewed)
                {
                    isHeld = false;
                    SafeCancelLost();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // watchdog stopped by Dispose
        }
    }

    private void SafeCancelLost()
    {
        try { lostСts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        isHeld = false;

        if (watchdogCts is not null)
        {
            await watchdogCts.CancelAsync().ConfigureAwait(false);
            if (watchdog is not null)
            {
                try { await watchdog.ConfigureAwait(false); }
                catch { /* watchdog faults are not actionable on dispose */ }
            }
            watchdogCts.Dispose();
        }

        try
        {
            await provider.ReleaseAsync(Key, ownerToken, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort release; the lease expires on its own if this fails
        }

        lostСts.Dispose();
    }
}
```

Note: the field `lostСts` uses an ASCII name in the actual file — write it as `lostCts` (the plan text may render a lookalike character; use plain ASCII `lostCts` everywhere). Rename consistently.

- [ ] **Step 4: Run tests, expect PASS**

Run: `dotnet test tests/Moongazing.OrionLock.Tests --filter DistributedLockHandleTests`
Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```
git add src/Moongazing.OrionLock/Internal/DistributedLockHandle.cs tests/Moongazing.OrionLock.Tests/DistributedLockHandleTests.cs
git commit -m "feat(orionlock): DistributedLockHandle with lease-renewal watchdog"
```

---

## Task 5: `DistributedLock` — blocking acquire and try-acquire

**Files:**
- Create: `src/Moongazing.OrionLock/DistributedLock.cs`
- Test: `tests/Moongazing.OrionLock.Tests/DistributedLockTests.cs`

> Reentrancy is added in Task 6; this task is acquire/try only.

- [ ] **Step 1: Write the failing tests**

`tests/Moongazing.OrionLock.Tests/DistributedLockTests.cs`:

```csharp
using System.Diagnostics;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class DistributedLockTests
{
    private static DistributedLock NewLock(out InMemoryLockProvider provider)
    {
        provider = new InMemoryLockProvider();
        return new DistributedLock(provider);
    }

    [Fact]
    public async Task TryAcquire_ShouldReturnHandle_WhenFree()
    {
        var l = NewLock(out _);
        await using var h = await l.TryAcquireAsync("k");
        Assert.NotNull(h);
        Assert.Equal("k", h!.Key);
    }

    [Fact]
    public async Task TryAcquire_ShouldReturnNull_WhenHeld()
    {
        var l = NewLock(out _);
        await using var first = await l.TryAcquireAsync("k");
        var second = await l.TryAcquireAsync("k");
        Assert.Null(second);
    }

    [Fact]
    public async Task Acquire_ShouldSucceed_WhenFree()
    {
        var l = NewLock(out _);
        await using var h = await l.AcquireAsync("k", TimeSpan.FromSeconds(30));
        Assert.Equal("k", h.Key);
    }

    [Fact]
    public async Task Acquire_ShouldThrowTimeout_WhenHeldPastWaitTimeout()
    {
        var l = NewLock(out _);
        await using var first = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<LockAcquisitionTimeoutException>(() =>
            l.AcquireAsync("k", new DistributedLockOptions
            {
                WaitTimeout = TimeSpan.FromMilliseconds(400),
                RetryInterval = TimeSpan.FromMilliseconds(50),
                AutoRenew = false,
            }));
        sw.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 350, 2000);
    }

    [Fact]
    public async Task Acquire_ShouldSucceed_WhenLockFreesBeforeWaitTimeout()
    {
        var l = NewLock(out _);
        var first = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        var release = Task.Run(async () => { await Task.Delay(150); await first.DisposeAsync(); });
        await using var second = await l.AcquireAsync("k", new DistributedLockOptions
        {
            WaitTimeout = TimeSpan.FromSeconds(5),
            RetryInterval = TimeSpan.FromMilliseconds(50),
            AutoRenew = false,
        });
        await release;
        Assert.Equal("k", second.Key);
    }

    [Fact]
    public async Task Acquire_ShouldThrowOperationCanceled_WhenTokenCancelled()
    {
        var l = NewLock(out _);
        await using var first = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            l.AcquireAsync("k", new DistributedLockOptions { WaitTimeout = TimeSpan.FromSeconds(30) }, cts.Token));
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test tests/Moongazing.OrionLock.Tests --filter DistributedLockTests`
Expected: build error.

- [ ] **Step 3: Create `DistributedLock.cs`**

```csharp
using System.Diagnostics;
using Moongazing.OrionLock.Internal;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock;

/// <summary>
/// The default <see cref="IDistributedLock"/>. Composes a backend <see cref="IDistributedLockProvider"/>
/// with a blocking-acquire retry loop and lease-renewing handles.
/// </summary>
public sealed class DistributedLock : IDistributedLock
{
    private readonly IDistributedLockProvider provider;

    /// <summary>Creates a lock over the given backend provider.</summary>
    public DistributedLock(IDistributedLockProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
    }

    /// <inheritdoc />
    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();

        var ownerToken = Guid.NewGuid().ToString("N");
        var acquired = await provider
            .TryAcquireAsync(key, ownerToken, options.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);

        return acquired ? new DistributedLockHandle(provider, key, ownerToken, options) : null;
    }

    /// <inheritdoc />
    public async Task<IDistributedLockHandle> AcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();

        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var handle = await TryAcquireAsync(key, options, cancellationToken).ConfigureAwait(false);
            if (handle is not null)
            {
                return handle;
            }

            if (deadline.Elapsed >= options.WaitTimeout)
            {
                throw new LockAcquisitionTimeoutException(key, deadline.Elapsed);
            }

            await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

`AcquireAsync` overload with `TimeSpan` — add it:

```csharp
    /// <summary>Acquires the lock using a lease of <paramref name="leaseDuration"/> and default wait/retry.</summary>
    public Task<IDistributedLockHandle> AcquireAsync(
        string key, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => AcquireAsync(key, new DistributedLockOptions { LeaseDuration = leaseDuration }, cancellationToken);
```

- [ ] **Step 4: Run tests, expect PASS**

Run: `dotnet test tests/Moongazing.OrionLock.Tests --filter DistributedLockTests`
Expected: 6 tests pass.

- [ ] **Step 5: Commit**

```
git add src/Moongazing.OrionLock/DistributedLock.cs tests/Moongazing.OrionLock.Tests/DistributedLockTests.cs
git commit -m "feat(orionlock): DistributedLock blocking acquire and try-acquire"
```

---

## Task 6: Reentrancy

**Files:**
- Create: `src/Moongazing.OrionLock/Internal/ReentrancyRegistry.cs`
- Create: `src/Moongazing.OrionLock/Internal/ReentrantLockHandle.cs`
- Modify: `src/Moongazing.OrionLock/DistributedLock.cs`
- Test: `tests/Moongazing.OrionLock.Tests/ReentrancyTests.cs`

> A single `DistributedLock` instance (a DI singleton) re-acquiring a key it already holds returns a counted nested handle without touching the backend. Only the outermost dispose releases.

- [ ] **Step 1: Write the failing tests**

`tests/Moongazing.OrionLock.Tests/ReentrancyTests.cs`:

```csharp
using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class ReentrancyTests
{
    [Fact]
    public async Task ReAcquire_SameKey_ShouldNotTouchBackend_AndShouldSucceed()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        await using var outer = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        await using var inner = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        Assert.Equal("k", inner.Key);
        Assert.Equal(1, provider.AcquireCount);   // backend hit once, not twice
    }

    [Fact]
    public async Task OuterDispose_ShouldReleaseBackend_OnlyAfterInnerDisposed()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        var outer = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });
        var inner = await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false });

        await inner.DisposeAsync();
        Assert.Equal(0, provider.ReleaseCount);   // inner dispose does not release

        await outer.DisposeAsync();
        Assert.Equal(1, provider.ReleaseCount);   // outer dispose releases
    }

    [Fact]
    public async Task TryAcquire_DifferentKey_ShouldHitBackend()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        await using var a = await l.AcquireAsync("k1", new DistributedLockOptions { AutoRenew = false });
        await using var b = await l.AcquireAsync("k2", new DistributedLockOptions { AutoRenew = false });

        Assert.Equal(2, provider.AcquireCount);
    }

    [Fact]
    public async Task ReAcquire_AfterFullRelease_ShouldHitBackendAgain()
    {
        var provider = new CountingProvider();
        var l = new DistributedLock(provider);

        await (await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false })).DisposeAsync();
        await (await l.AcquireAsync("k", new DistributedLockOptions { AutoRenew = false })).DisposeAsync();

        Assert.Equal(2, provider.AcquireCount);
    }

    private sealed class CountingProvider : Moongazing.OrionLock.Providers.IDistributedLockProvider
    {
        public int AcquireCount;
        public int ReleaseCount;

        public Task<bool> TryAcquireAsync(string k, string o, TimeSpan d, CancellationToken c)
        {
            Interlocked.Increment(ref AcquireCount);
            return Task.FromResult(true);
        }

        public Task<bool> TryRenewAsync(string k, string o, TimeSpan d, CancellationToken c) => Task.FromResult(true);

        public Task ReleaseAsync(string k, string o, CancellationToken c)
        {
            Interlocked.Increment(ref ReleaseCount);
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test tests/Moongazing.OrionLock.Tests --filter ReentrancyTests`
Expected: failures — `AcquireCount` is 2 (no reentrancy yet).

- [ ] **Step 3: Create `Internal/ReentrancyRegistry.cs`**

```csharp
using System.Collections.Concurrent;

namespace Moongazing.OrionLock.Internal;

/// <summary>
/// Tracks, per <see cref="DistributedLock"/> instance, which keys are currently held so a
/// re-acquisition of the same key collapses into a counted nested handle instead of a second
/// backend call. Process-local by design — reentrancy must not cross process boundaries.
/// </summary>
public sealed class ReentrancyRegistry
{
    private sealed class Entry
    {
        public required IDistributedLockHandle RealHandle { get; init; }
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Entry> held = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>
    /// If <paramref name="key"/> is already held, increments its count and returns a nested handle.
    /// Otherwise returns null and the caller must acquire the backend lock, then call
    /// <see cref="Register"/>.
    /// </summary>
    public IDistributedLockHandle? TryEnter(string key)
    {
        lock (gate)
        {
            if (held.TryGetValue(key, out var entry))
            {
                entry.Count++;
                return new ReentrantLockHandle(this, key, entry.RealHandle);
            }
            return null;
        }
    }

    /// <summary>Records a freshly acquired backend handle and returns the outermost nested handle.</summary>
    public IDistributedLockHandle Register(string key, IDistributedLockHandle realHandle)
    {
        lock (gate)
        {
            var entry = new Entry { RealHandle = realHandle, Count = 1 };
            held[key] = entry;
            return new ReentrantLockHandle(this, key, realHandle);
        }
    }

    /// <summary>
    /// Decrements the count for <paramref name="key"/>. Returns true when the count reaches zero,
    /// meaning the caller must dispose the real backend handle.
    /// </summary>
    public bool Exit(string key)
    {
        lock (gate)
        {
            if (held.TryGetValue(key, out var entry))
            {
                entry.Count--;
                if (entry.Count <= 0)
                {
                    held.TryRemove(key, out _);
                    return true;
                }
            }
            return false;
        }
    }
}
```

- [ ] **Step 4: Create `Internal/ReentrantLockHandle.cs`**

```csharp
namespace Moongazing.OrionLock.Internal;

/// <summary>
/// A nested handle returned for a reentrant (same key, same process) acquisition. Its
/// <see cref="DisposeAsync"/> decrements the reentrancy count; the real backend handle is
/// disposed only when the outermost handle is disposed.
/// </summary>
public sealed class ReentrantLockHandle : IDistributedLockHandle
{
    private readonly ReentrancyRegistry registry;
    private readonly IDistributedLockHandle realHandle;
    private int disposed;

    /// <summary>Creates a nested handle.</summary>
    public ReentrantLockHandle(ReentrancyRegistry registry, string key, IDistributedLockHandle realHandle)
    {
        this.registry = registry;
        Key = key;
        this.realHandle = realHandle;
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public bool IsHeld => realHandle.IsHeld;

    /// <inheritdoc />
    public CancellationToken LostToken => realHandle.LostToken;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (registry.Exit(Key))
        {
            await realHandle.DisposeAsync().ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 5: Wire reentrancy into `DistributedLock`**

In `src/Moongazing.OrionLock/DistributedLock.cs`, add a `ReentrancyRegistry` field and route both acquire paths through it. Add `using Moongazing.OrionLock.Internal;` (already present for `DistributedLockHandle`). Replace the class body so the field and the two methods become:

```csharp
    private readonly IDistributedLockProvider provider;
    private readonly ReentrancyRegistry reentrancy = new();

    /// <summary>Creates a lock over the given backend provider.</summary>
    public DistributedLock(IDistributedLockProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        this.provider = provider;
    }

    /// <inheritdoc />
    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();

        var nested = reentrancy.TryEnter(key);
        if (nested is not null)
        {
            return nested;
        }

        var ownerToken = Guid.NewGuid().ToString("N");
        var acquired = await provider
            .TryAcquireAsync(key, ownerToken, options.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);

        if (!acquired)
        {
            return null;
        }

        var real = new DistributedLockHandle(provider, key, ownerToken, options);
        return reentrancy.Register(key, real);
    }
```

`AcquireAsync` and the `TimeSpan` overload are unchanged — `AcquireAsync`'s retry loop calls `TryAcquireAsync`, which now handles reentrancy.

> Race note: `TryEnter` / `Register` are guarded by the registry's lock, but the backend `TryAcquireAsync` runs outside it. Two threads racing the same fresh key could both miss `TryEnter`, both call the backend, and one loses. The loser's backend call returns false (the backend is the real arbiter) and it returns null / retries — correct. The only cost is a redundant backend attempt under a rare race, which is acceptable.

- [ ] **Step 6: Run tests, expect PASS**

Run: `dotnet test tests/Moongazing.OrionLock.Tests`
Expected: all core tests pass (`ContractTests`, `DistributedLockHandleTests`, `DistributedLockTests`, `ReentrancyTests`).

- [ ] **Step 7: Commit**

```
git add src/Moongazing.OrionLock/Internal/ReentrancyRegistry.cs src/Moongazing.OrionLock/Internal/ReentrantLockHandle.cs src/Moongazing.OrionLock/DistributedLock.cs tests/Moongazing.OrionLock.Tests/ReentrancyTests.cs
git commit -m "feat(orionlock): same-process reentrancy via counted nested handles"
```

---

## Task 7: DI — `AddOrionLock` and `OrionLockBuilder`

**Files:**
- Create: `src/Moongazing.OrionLock/DependencyInjection/OrionLockBuilder.cs`
- Create: `src/Moongazing.OrionLock/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/Moongazing.OrionLock.Tests/DependencyInjectionTests.cs`

> `AddOrionLock` registers the core. A backend package adds a `Use*` extension on `OrionLockBuilder` that registers an `IDistributedLockProvider`. `DistributedLock` is registered as a singleton resolving whatever provider is registered.

- [ ] **Step 1: Write the failing tests**

`tests/Moongazing.OrionLock.Tests/DependencyInjectionTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddOrionLock_ShouldRegister_IDistributedLock_AsSingleton()
    {
        var services = new ServiceCollection();
        services.AddOrionLock();
        services.AddSingleton<IDistributedLockProvider, InMemoryLockProvider>();

        using var sp = services.BuildServiceProvider();
        var a = sp.GetRequiredService<IDistributedLock>();
        var b = sp.GetRequiredService<IDistributedLock>();

        Assert.IsType<DistributedLock>(a);
        Assert.Same(a, b);
    }

    [Fact]
    public void AddOrionLock_ShouldReturnBuilder_ExposingServices()
    {
        var services = new ServiceCollection();
        var builder = services.AddOrionLock();
        Assert.Same(services, builder.Services);
    }

    [Fact]
    public async Task ResolvedLock_ShouldFunction_OverRegisteredProvider()
    {
        var services = new ServiceCollection();
        services.AddOrionLock();
        services.AddSingleton<IDistributedLockProvider, InMemoryLockProvider>();

        using var sp = services.BuildServiceProvider();
        var locker = sp.GetRequiredService<IDistributedLock>();

        await using var h = await locker.AcquireAsync("k", TimeSpan.FromSeconds(30));
        Assert.Equal("k", h.Key);
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test tests/Moongazing.OrionLock.Tests --filter DependencyInjectionTests`
Expected: build error.

- [ ] **Step 3: Create `DependencyInjection/OrionLockBuilder.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Moongazing.OrionLock.DependencyInjection;

/// <summary>
/// Returned by <c>AddOrionLock</c>. Backend packages add <c>Use*</c> extension methods on this
/// type to register an <see cref="Providers.IDistributedLockProvider"/>.
/// </summary>
public sealed class OrionLockBuilder
{
    /// <summary>Creates a builder over the given service collection.</summary>
    public OrionLockBuilder(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }
}
```

- [ ] **Step 4: Create `DependencyInjection/ServiceCollectionExtensions.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.DependencyInjection;

/// <summary>DI extensions for OrionLock.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OrionLock core. Call a backend extension on the returned builder
    /// (for example <c>UseRedis</c> or <c>UseEntityFrameworkCore</c>) to supply a provider.
    /// </summary>
    public static OrionLockBuilder AddOrionLock(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDistributedLock>(sp =>
            new DistributedLock(sp.GetRequiredService<IDistributedLockProvider>()));

        return new OrionLockBuilder(services);
    }
}
```

- [ ] **Step 5: Run tests, expect PASS**

Run: `dotnet test tests/Moongazing.OrionLock.Tests --filter DependencyInjectionTests`
Expected: 3 tests pass.

- [ ] **Step 6: Commit**

```
git add src/Moongazing.OrionLock/DependencyInjection tests/Moongazing.OrionLock.Tests/DependencyInjectionTests.cs
git commit -m "feat(orionlock): AddOrionLock DI registration and OrionLockBuilder"
```

---

## Task 8: `OrionLock.Testing` — `UseInMemory` builder extension

**Files:**
- Create: `src/Moongazing.OrionLock.Testing/OrionLockTestingBuilderExtensions.cs`
- Test: `tests/Moongazing.OrionLock.Testing.Tests/UseInMemoryTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/Moongazing.OrionLock.Testing.Tests/UseInMemoryTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Testing.Tests;

public class UseInMemoryTests
{
    [Fact]
    public async Task UseInMemory_ShouldRegisterWorkingInMemoryLock()
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseInMemory();

        using var sp = services.BuildServiceProvider();
        Assert.IsType<InMemoryLockProvider>(sp.GetRequiredService<IDistributedLockProvider>());

        var locker = sp.GetRequiredService<IDistributedLock>();
        await using var h = await locker.AcquireAsync("k", TimeSpan.FromSeconds(30));
        Assert.Equal("k", h.Key);
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test tests/Moongazing.OrionLock.Testing.Tests --filter UseInMemoryTests`
Expected: build error.

- [ ] **Step 3: Create `OrionLockTestingBuilderExtensions.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.Testing;

/// <summary>Registers the in-memory OrionLock backend for tests.</summary>
public static class OrionLockTestingBuilderExtensions
{
    /// <summary>Uses an in-process <see cref="InMemoryLockProvider"/> — for tests only.</summary>
    public static OrionLockBuilder UseInMemory(this OrionLockBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IDistributedLockProvider, InMemoryLockProvider>();
        return builder;
    }
}
```

- [ ] **Step 4: Run tests, expect PASS; then full solution**

Run: `dotnet test tests/Moongazing.OrionLock.Testing.Tests` then `dotnet test`
Expected: all tests pass.

- [ ] **Step 5: Commit**

```
git add src/Moongazing.OrionLock.Testing/OrionLockTestingBuilderExtensions.cs tests/Moongazing.OrionLock.Testing.Tests/UseInMemoryTests.cs
git commit -m "feat(orionlock): OrionLock.Testing UseInMemory builder extension"
```

---

## Task 9: `OrionLock.Redis` — Redis backend

**Files:**
- Create: `src/Moongazing.OrionLock.Redis/RedisLockOptions.cs`
- Create: `src/Moongazing.OrionLock.Redis/RedisLockProvider.cs`
- Create: `src/Moongazing.OrionLock.Redis/OrionLockRedisBuilderExtensions.cs`
- Delete: `src/Moongazing.OrionLock.Redis/_Placeholder.cs`
- Test: `tests/Moongazing.OrionLock.Redis.Tests/RedisLockProviderTests.cs`

> The Redis lock: `SET key ownerToken NX PX leaseMs` to acquire; compare-and-extend Lua for renew; compare-and-delete Lua for release.

- [ ] **Step 1: Write the failing tests (Testcontainers-backed)**

`tests/Moongazing.OrionLock.Redis.Tests/RedisLockProviderTests.cs`:

```csharp
using Moongazing.OrionLock.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Moongazing.OrionLock.Redis.Tests;

public sealed class RedisLockProviderTests : IAsyncLifetime
{
    private readonly RedisContainer container = new RedisBuilder().Build();
    private IConnectionMultiplexer mux = default!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        mux = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await mux.DisposeAsync();
        await container.DisposeAsync();
    }

    private RedisLockProvider NewProvider() => new(mux, new RedisLockOptions());

    [Fact]
    public async Task TryAcquire_ShouldSucceedThenBlockSecondOwner()
    {
        var p = NewProvider();
        Assert.True(await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldSucceed_AfterLeaseExpires()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromMilliseconds(200), default);
        await Task.Delay(400);
        Assert.True(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldExtendForOwner_AndRejectNonOwner()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(2), default);
        Assert.True(await p.TryRenewAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryRenewAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldOnlyReleaseForOwner()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync("k", "owner-2", default);
        Assert.False(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
        await p.ReleaseAsync("k", "owner-1", default);
        Assert.True(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test tests/Moongazing.OrionLock.Redis.Tests`
Expected: build error.

- [ ] **Step 3: Create `RedisLockOptions.cs`**

```csharp
namespace Moongazing.OrionLock.Redis;

/// <summary>Configuration for the Redis OrionLock backend.</summary>
public sealed class RedisLockOptions
{
    /// <summary>Prefix prepended to every lock key in Redis. Default <c>orionlock:</c>.</summary>
    public string KeyPrefix { get; set; } = "orionlock:";

    /// <summary>The Redis database index. Default -1 (the connection's default database).</summary>
    public int Database { get; set; } = -1;
}
```

- [ ] **Step 4: Create `RedisLockProvider.cs`**

```csharp
using Moongazing.OrionLock.Providers;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis;

/// <summary>
/// Redis-backed <see cref="IDistributedLockProvider"/>. Acquire is <c>SET key token NX PX</c>;
/// renew and release are owner-checked Lua scripts (compare-and-extend, compare-and-delete).
/// </summary>
public sealed class RedisLockProvider : IDistributedLockProvider
{
    private const string RenewScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) else return 0 end";

    private const string ReleaseScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";

    private readonly IConnectionMultiplexer multiplexer;
    private readonly RedisLockOptions options;

    /// <summary>Creates the provider over an existing Redis connection.</summary>
    public RedisLockProvider(IConnectionMultiplexer multiplexer, RedisLockOptions options)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentNullException.ThrowIfNull(options);
        this.multiplexer = multiplexer;
        this.options = options;
    }

    private IDatabase Db => multiplexer.GetDatabase(options.Database);

    private RedisKey Key(string key) => options.KeyPrefix + key;

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => await Db.StringSetAsync(Key(key), ownerToken, leaseDuration, when: When.NotExists).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var result = await Db.ScriptEvaluateAsync(
            RenewScript,
            new RedisKey[] { Key(key) },
            new RedisValue[] { ownerToken, (long)leaseDuration.TotalMilliseconds }).ConfigureAwait(false);
        return (long)result == 1;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
        => await Db.ScriptEvaluateAsync(
            ReleaseScript,
            new RedisKey[] { Key(key) },
            new RedisValue[] { ownerToken }).ConfigureAwait(false);
}
```

- [ ] **Step 5: Create `OrionLockRedisBuilderExtensions.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;
using StackExchange.Redis;

namespace Moongazing.OrionLock.Redis;

/// <summary>Registers the Redis OrionLock backend.</summary>
public static class OrionLockRedisBuilderExtensions
{
    /// <summary>Uses Redis as the OrionLock backend, connecting with <paramref name="connectionString"/>.</summary>
    public static OrionLockBuilder UseRedis(
        this OrionLockBuilder builder, string connectionString, Action<RedisLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new RedisLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(connectionString));
        builder.Services.TryAddSingleton<IDistributedLockProvider>(
            sp => new RedisLockProvider(sp.GetRequiredService<IConnectionMultiplexer>(), options));

        return builder;
    }

    /// <summary>Uses Redis as the OrionLock backend over an already-registered <see cref="IConnectionMultiplexer"/>.</summary>
    public static OrionLockBuilder UseRedis(
        this OrionLockBuilder builder, Action<RedisLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new RedisLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<IDistributedLockProvider>(
            sp => new RedisLockProvider(sp.GetRequiredService<IConnectionMultiplexer>(), options));

        return builder;
    }
}
```

- [ ] **Step 6: Delete the Redis placeholder, run tests, expect PASS**

Delete `src/Moongazing.OrionLock.Redis/_Placeholder.cs`.
Run: `dotnet test tests/Moongazing.OrionLock.Redis.Tests`
Expected: 4 tests pass. (Testcontainers needs Docker. If Docker is unavailable in the execution environment, report this clearly — the CI pipeline provides Docker. Do not delete or weaken the tests.)

- [ ] **Step 7: Commit**

```
git add src/Moongazing.OrionLock.Redis tests/Moongazing.OrionLock.Redis.Tests/RedisLockProviderTests.cs
git commit -m "feat(orionlock): Redis backend - RedisLockProvider and UseRedis"
```

---

## Task 10: `OrionLock.EntityFrameworkCore` — entity and configuration

**Files:**
- Create: `src/Moongazing.OrionLock.EntityFrameworkCore/OrionLockRow.cs`
- Create: `src/Moongazing.OrionLock.EntityFrameworkCore/OrionLockRowEntityTypeConfiguration.cs`
- Delete: `src/Moongazing.OrionLock.EntityFrameworkCore/_Placeholder.cs`

- [ ] **Step 1: Create `OrionLockRow.cs`**

```csharp
namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// Persistent row backing <see cref="EfCoreLockProvider"/>. One row per lock key in the
/// <c>OrionLock_Locks</c> table.
/// </summary>
public sealed class OrionLockRow
{
    /// <summary>The lock key (primary key).</summary>
    public string Key { get; set; } = default!;

    /// <summary>The current owner token, or null when the lock is free.</summary>
    public string? OwnerToken { get; set; }

    /// <summary>The lease deadline. A row whose deadline has passed is free for any caller.</summary>
    public DateTime ExpiresOnUtc { get; set; }
}
```

- [ ] **Step 2: Create `OrionLockRowEntityTypeConfiguration.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// EF Core mapping for <see cref="OrionLockRow"/>. Apply inside <c>OnModelCreating</c>:
/// <c>modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());</c>.
/// </summary>
public sealed class OrionLockRowEntityTypeConfiguration : IEntityTypeConfiguration<OrionLockRow>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OrionLockRow> builder)
    {
        builder.ToTable("OrionLock_Locks");
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(200).IsRequired();
        builder.Property(x => x.OwnerToken).HasMaxLength(64);
        builder.Property(x => x.ExpiresOnUtc);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Moongazing.OrionLock.EntityFrameworkCore`
Expected: success. Delete `_Placeholder.cs` before building.

- [ ] **Step 4: Commit**

```
git add src/Moongazing.OrionLock.EntityFrameworkCore/OrionLockRow.cs src/Moongazing.OrionLock.EntityFrameworkCore/OrionLockRowEntityTypeConfiguration.cs
git commit -m "feat(orionlock): EF Core OrionLockRow entity and configuration"
```

---

## Task 11: `OrionLock.EntityFrameworkCore` — provider and `UseEntityFrameworkCore`

**Files:**
- Create: `src/Moongazing.OrionLock.EntityFrameworkCore/EfCoreLockProvider.cs`
- Create: `src/Moongazing.OrionLock.EntityFrameworkCore/OrionLockEfCoreBuilderExtensions.cs`
- Test: `tests/Moongazing.OrionLock.EntityFrameworkCore.Tests/EfCoreLockProviderTests.cs`

> The provider resolves a `DbContext` per call via `IServiceScopeFactory`. Acquire is an atomic conditional `UPDATE`, then an `INSERT ... WHERE NOT EXISTS`, then an owner-check `SELECT` — the proven `SkipLockedDistributedLock` pattern from OrionGuard. All SQL via `ExecuteSqlInterpolatedAsync`, provider-agnostic.

- [ ] **Step 1: Write the failing tests**

`tests/Moongazing.OrionLock.EntityFrameworkCore.Tests/EfCoreLockProviderTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.EntityFrameworkCore;

namespace Moongazing.OrionLock.EntityFrameworkCore.Tests;

public sealed class LockTestDbContext(DbContextOptions<LockTestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration());
}

public sealed class EfCoreLockProviderTests : IAsyncLifetime
{
    private SqliteConnection connection = default!;
    private IServiceProvider services = default!;

    public Task InitializeAsync()
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var sc = new ServiceCollection();
        sc.AddDbContext<LockTestDbContext>(o => o.UseSqlite(connection));
        sc.AddScoped<DbContext>(sp => sp.GetRequiredService<LockTestDbContext>());
        services = sc.BuildServiceProvider();

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<LockTestDbContext>().Database.EnsureCreated();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        connection.Dispose();
        (services as IDisposable)?.Dispose();
        return Task.CompletedTask;
    }

    private EfCoreLockProvider NewProvider()
        => new(services.GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task TryAcquire_ShouldSucceedThenBlockSecondOwner()
    {
        var p = NewProvider();
        Assert.True(await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldSucceed_AfterLeaseExpires()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromMilliseconds(50), default);
        await Task.Delay(150);
        Assert.True(await p.TryAcquireAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldExtendForOwner_AndRejectNonOwner()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(2), default);
        Assert.True(await p.TryRenewAsync("k", "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryRenewAsync("k", "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldOnlyReleaseForOwner()
    {
        var p = NewProvider();
        await p.TryAcquireAsync("k", "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync("k", "owner-2", default);
        Assert.False(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
        await p.ReleaseAsync("k", "owner-1", default);
        Assert.True(await p.TryAcquireAsync("k", "owner-3", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers()
    {
        var p = NewProvider();
        var tasks = Enumerable.Range(0, 5)
            .Select(i => p.TryAcquireAsync("k", $"owner-{i}", TimeSpan.FromSeconds(30), default))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r));
    }
}
```

The test csproj needs `Microsoft.Data.Sqlite` — it is pulled transitively by `Microsoft.EntityFrameworkCore.Sqlite` (added in Task 1 Step 3). If `SqliteConnection` does not resolve, add `<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />` explicitly.

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test tests/Moongazing.OrionLock.EntityFrameworkCore.Tests`
Expected: build error.

- [ ] **Step 3: Create `EfCoreLockProvider.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>
/// EF Core lock-table <see cref="IDistributedLockProvider"/>. Each call resolves a scoped
/// <see cref="DbContext"/> and runs provider-agnostic SQL against the <c>OrionLock_Locks</c> table.
/// </summary>
public sealed class EfCoreLockProvider : IDistributedLockProvider
{
    private readonly IServiceScopeFactory scopeFactory;

    /// <summary>Creates the provider. A scoped <see cref="DbContext"/> is resolved per call.</summary>
    public EfCoreLockProvider(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        this.scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expires = now + leaseDuration;

        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();

        // Take a free or expired row.
        var updated = await ctx.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE OrionLock_Locks
                  SET OwnerToken = {ownerToken}, ExpiresOnUtc = {expires}
                WHERE Key = {key} AND (OwnerToken IS NULL OR ExpiresOnUtc <= {now})",
            cancellationToken).ConfigureAwait(false);

        if (updated == 0)
        {
            // First-ever use of this key: insert if absent.
            try
            {
                await ctx.Database.ExecuteSqlInterpolatedAsync(
                    $@"INSERT INTO OrionLock_Locks (Key, OwnerToken, ExpiresOnUtc)
                       SELECT {key}, {ownerToken}, {expires}
                       WHERE NOT EXISTS (SELECT 1 FROM OrionLock_Locks WHERE Key = {key})",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                return false;
            }
        }

        // Owner-check: did this caller win?
        var owner = await ctx.Set<OrionLockRow>().AsNoTracking()
            .Where(x => x.Key == key)
            .Select(x => x.OwnerToken)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return owner == ownerToken;
    }

    /// <inheritdoc />
    public async Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var expires = DateTime.UtcNow + leaseDuration;
        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();

        var updated = await ctx.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE OrionLock_Locks
                  SET ExpiresOnUtc = {expires}
                WHERE Key = {key} AND OwnerToken = {ownerToken}",
            cancellationToken).ConfigureAwait(false);

        return updated > 0;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DbContext>();

        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE OrionLock_Locks
                  SET OwnerToken = NULL, ExpiresOnUtc = {now}
                WHERE Key = {key} AND OwnerToken = {ownerToken}",
            cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Create `OrionLockEfCoreBuilderExtensions.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.EntityFrameworkCore;

/// <summary>Registers the EF Core lock-table OrionLock backend.</summary>
public static class OrionLockEfCoreBuilderExtensions
{
    /// <summary>
    /// Uses an EF Core lock table as the OrionLock backend, resolving <typeparamref name="TDbContext"/>
    /// per acquisition. <typeparamref name="TDbContext"/> must apply
    /// <see cref="OrionLockRowEntityTypeConfiguration"/> in <c>OnModelCreating</c>.
    /// </summary>
    public static OrionLockBuilder UseEntityFrameworkCore<TDbContext>(this OrionLockBuilder builder)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddScoped<DbContext>(sp => sp.GetRequiredService<TDbContext>());
        builder.Services.TryAddSingleton<IDistributedLockProvider>(
            sp => new EfCoreLockProvider(sp.GetRequiredService<IServiceScopeFactory>()));

        return builder;
    }
}
```

- [ ] **Step 5: Run tests, expect PASS**

Run: `dotnet test tests/Moongazing.OrionLock.EntityFrameworkCore.Tests`
Expected: 5 tests pass.

- [ ] **Step 6: Commit**

```
git add src/Moongazing.OrionLock.EntityFrameworkCore tests/Moongazing.OrionLock.EntityFrameworkCore.Tests/EfCoreLockProviderTests.cs
git commit -m "feat(orionlock): EF Core backend - EfCoreLockProvider and UseEntityFrameworkCore"
```

---

## Task 12: OpenTelemetry instrumentation

**Files:**
- Create: `src/Moongazing.OrionLock/Diagnostics/OrionLockDiagnostics.cs`
- Modify: `src/Moongazing.OrionLock/DistributedLock.cs`
- Test: `tests/Moongazing.OrionLock.Tests/DiagnosticsTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/Moongazing.OrionLock.Tests/DiagnosticsTests.cs`:

```csharp
using System.Diagnostics;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Diagnostics;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Tests;

public class DiagnosticsTests
{
    [Fact]
    public async Task Acquire_ShouldEmitActivity_OnTheOrionLockSource()
    {
        var activities = new System.Collections.Concurrent.ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == OrionLockDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var locker = new DistributedLock(new InMemoryLockProvider());
        await using (await locker.AcquireAsync("k", TimeSpan.FromSeconds(30))) { }

        Assert.Contains(activities, a => a.DisplayName.Contains("k", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2: Run, expect failure**

Run: `dotnet test tests/Moongazing.OrionLock.Tests --filter DiagnosticsTests`
Expected: build error.

- [ ] **Step 3: Create `Diagnostics/OrionLockDiagnostics.cs`**

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Moongazing.OrionLock.Diagnostics;

/// <summary>OrionLock OpenTelemetry instrumentation: an <see cref="ActivitySource"/> and a <see cref="Meter"/>.</summary>
public static class OrionLockDiagnostics
{
    /// <summary>The OrionLock activity source name.</summary>
    public const string ActivitySourceName = "Moongazing.OrionLock";

    /// <summary>The OrionLock meter name.</summary>
    public const string MeterName = "Moongazing.OrionLock";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.1.0");

    private static readonly Meter Meter = new(MeterName, "0.1.0");

    internal static readonly Counter<long> Acquisitions = Meter.CreateCounter<long>("orionlock.acquisitions");
    internal static readonly Counter<long> Contentions = Meter.CreateCounter<long>("orionlock.contentions");
    internal static readonly Counter<long> LeasesLost = Meter.CreateCounter<long>("orionlock.lease.lost");
    internal static readonly Histogram<double> AcquireDuration = Meter.CreateHistogram<double>("orionlock.acquire.duration");
}
```

- [ ] **Step 4: Instrument `DistributedLock`**

In `DistributedLock.cs`, add `using System.Diagnostics;` and `using Moongazing.OrionLock.Diagnostics;`. Wrap `AcquireAsync` so it opens an activity, records the duration histogram, and increments counters. Replace `AcquireAsync(string, DistributedLockOptions?, CancellationToken)` with:

```csharp
    /// <inheritdoc />
    public async Task<IDistributedLockHandle> AcquireAsync(
        string key, DistributedLockOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new DistributedLockOptions();

        using var activity = OrionLockDiagnostics.ActivitySource.StartActivity($"OrionLock.Acquire {key}");
        activity?.SetTag("orionlock.key", key);

        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var handle = await TryAcquireAsync(key, options, cancellationToken).ConfigureAwait(false);
            if (handle is not null)
            {
                activity?.SetTag("orionlock.outcome", "acquired");
                OrionLockDiagnostics.Acquisitions.Add(1);
                OrionLockDiagnostics.AcquireDuration.Record(deadline.Elapsed.TotalMilliseconds);
                return handle;
            }

            OrionLockDiagnostics.Contentions.Add(1);

            if (deadline.Elapsed >= options.WaitTimeout)
            {
                activity?.SetTag("orionlock.outcome", "timeout");
                throw new LockAcquisitionTimeoutException(key, deadline.Elapsed);
            }

            await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }
```

`TryAcquireAsync` stays as in Task 6. (The `orionlock.lease.lost` counter is incremented from the handle's watchdog — optionally wire it in `DistributedLockHandle` where `isHeld` flips to false: add `OrionLockDiagnostics.LeasesLost.Add(1);` next to `SafeCancelLost()`. Include that one-line addition in this task.)

- [ ] **Step 5: Run tests, expect PASS**

Run: `dotnet test tests/Moongazing.OrionLock.Tests`
Expected: all core tests pass including `DiagnosticsTests`.

- [ ] **Step 6: Commit**

```
git add src/Moongazing.OrionLock/Diagnostics src/Moongazing.OrionLock/DistributedLock.cs src/Moongazing.OrionLock/Internal/DistributedLockHandle.cs tests/Moongazing.OrionLock.Tests/DiagnosticsTests.cs
git commit -m "feat(orionlock): OpenTelemetry ActivitySource and Meter instrumentation"
```

---

## Task 13: Benchmarks

**Files:**
- Create: `bench/Moongazing.OrionLock.Benchmarks/Program.cs`
- Create: `bench/Moongazing.OrionLock.Benchmarks/AcquireBenchmarks.cs`
- Delete: `bench/Moongazing.OrionLock.Benchmarks/Program.cs` placeholder content (replace it)

- [ ] **Step 1: Create `AcquireBenchmarks.cs`**

```csharp
using BenchmarkDotNet.Attributes;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Benchmarks;

[MemoryDiagnoser]
public class AcquireBenchmarks
{
    private DistributedLock locker = default!;

    [GlobalSetup]
    public void Setup() => locker = new DistributedLock(new InMemoryLockProvider());

    [Benchmark]
    public async Task UncontendedAcquireRelease()
    {
        await using var h = await locker.AcquireAsync("bench-key",
            new DistributedLockOptions { AutoRenew = false });
    }
}
```

- [ ] **Step 2: Replace `Program.cs`**

```csharp
using BenchmarkDotNet.Running;
using Moongazing.OrionLock.Benchmarks;

BenchmarkRunner.Run<AcquireBenchmarks>();
```

- [ ] **Step 3: Build (do not run the full benchmark)**

Run: `dotnet build bench/Moongazing.OrionLock.Benchmarks -c Release`
Expected: success.

- [ ] **Step 4: Commit**

```
git add bench/Moongazing.OrionLock.Benchmarks
git commit -m "bench(orionlock): uncontended acquire/release benchmark"
```

---

## Task 14: Sample application

**Files:**
- Create: `sample/Moongazing.OrionLock.Sample/Program.cs` (replace placeholder)

- [ ] **Step 1: Replace `Program.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Testing;

var services = new ServiceCollection();
services.AddOrionLock().UseInMemory();
using var sp = services.BuildServiceProvider();

var locker = sp.GetRequiredService<IDistributedLock>();

// Blocking acquire with a 30s lease and the auto-renewal watchdog.
await using (var handle = await locker.AcquireAsync("order:42", TimeSpan.FromSeconds(30)))
{
    Console.WriteLine($"Acquired '{handle.Key}'. IsHeld={handle.IsHeld}");

    // Reentrant re-acquire of the same key returns a nested handle, no second backend call.
    await using (var nested = await locker.AcquireAsync("order:42", TimeSpan.FromSeconds(30)))
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
```

- [ ] **Step 2: Build and run**

Run: `dotnet run --project sample/Moongazing.OrionLock.Sample -c Release`
Expected: prints the acquire / reenter / critical-section / try-acquire lines.

- [ ] **Step 3: Commit**

```
git add sample/Moongazing.OrionLock.Sample
git commit -m "sample(orionlock): end-to-end sample exercising acquire, reentrancy, try-acquire"
```

---

## Task 15: Documentation

**Files:**
- Create: `README.md`
- Create: `CHANGELOG.md`
- Create: `LICENSE.txt`
- Create: `docs/lease-and-renewal.md`
- Create: `docs/migrations/orionlock-locks-table.md`

- [ ] **Step 1: Create `LICENSE.txt`**

Standard MIT license text, copyright `2026 Tunahan Ali Ozturk`.

- [ ] **Step 2: Create `README.md`**

Sections in order:
- Title `OrionLock`, one-line pitch: "Distributed locking for .NET — Redis and EF Core backends, with reentrancy and lease auto-renewal."
- Badges (NuGet `OrionLock`, license, .NET target) mirroring the family style.
- **Quick start**: `dotnet add package OrionLock` plus `OrionLock.Redis` (or `OrionLock.EntityFrameworkCore`); the `AddOrionLock().UseRedis(...)` wiring; an `await using var handle = await locker.AcquireAsync("order:42", TimeSpan.FromSeconds(30));` example.
- **Acquire vs TryAcquire** — blocking with `WaitTimeout` vs immediate.
- **Lease and renewal** — short paragraph, link to `docs/lease-and-renewal.md`.
- **Reentrancy** — same-process re-acquire returns a counted nested handle.
- **Backends** — `OrionLock.Redis`, `OrionLock.EntityFrameworkCore`, `OrionLock.Testing`.
- **More from the Orion family** — bullet list linking OrionGuard, OrionAudit, OrionKey.
- No emojis, no buzzwords.

- [ ] **Step 3: Create `CHANGELOG.md`**

```markdown
# Changelog

All notable changes to OrionLock are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-05-21

### Added

- `IDistributedLock` with blocking `AcquireAsync` (wait + retry) and non-blocking `TryAcquireAsync`.
- `IDistributedLockHandle` with `IsHeld` and a `LostToken` that trips when the lease is lost.
- Background lease auto-renewal watchdog (renews at one third of the lease duration).
- Same-process reentrancy — re-acquiring a held key returns a counted nested handle.
- `OrionLock.Redis` backend (`SET NX PX` acquire, owner-checked Lua renew/release).
- `OrionLock.EntityFrameworkCore` backend (provider-agnostic `OrionLock_Locks` table).
- `OrionLock.Testing` in-memory backend.
- OpenTelemetry `ActivitySource` and `Meter` (`Moongazing.OrionLock`).
- `AddOrionLock()` DI with `UseRedis` / `UseEntityFrameworkCore` / `UseInMemory`.
```

- [ ] **Step 4: Create `docs/lease-and-renewal.md`**

Explain: what a lease is; `LeaseDuration` vs `WaitTimeout` vs `RetryInterval`; how the watchdog renews at `LeaseDuration / 3`; what happens on renewal failure (`IsHeld` flips, `LostToken` trips); how a critical section should observe `LostToken`; guidance on choosing `LeaseDuration` (longer than the critical section's expected wall-clock, short enough that a crashed holder frees the lock reasonably fast).

- [ ] **Step 5: Create `docs/migrations/orionlock-locks-table.md`**

EF Core migration template for `OrionLock_Locks`, with reference DDL for PostgreSQL, SQL Server, MySQL, and SQLite. Table columns: `Key` (PK, string/varchar 200), `OwnerToken` (string/varchar 64, nullable), `ExpiresOnUtc` (timestamp/datetime). Include the `modelBuilder.ApplyConfiguration(new OrionLockRowEntityTypeConfiguration())` instruction and the `dotnet ef migrations add` command.

- [ ] **Step 6: Commit**

```
git add README.md CHANGELOG.md LICENSE.txt docs/lease-and-renewal.md docs/migrations/orionlock-locks-table.md
git commit -m "docs(orionlock): README, CHANGELOG, license, lease and migration guides"
```

---

## Task 16: CI/CD, package metadata, final verification

**Files:**
- Create: `.github/workflows/ci-cd.yml`
- Modify: the four `src` csproj files (per-package READMEs)
- Create: `src/Moongazing.OrionLock*/docs/README.md` (one per packable project)

- [ ] **Step 1: Create `.github/workflows/ci-cd.yml`**

```yaml
name: CI/CD

on:
  push:
    branches: [ main, master ]
  pull_request:
    branches: [ main, master ]
  release:
    types: [ published ]

env:
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: true
  DOTNET_CLI_TELEMETRY_OPTOUT: true
  SOLUTION_PATH: Moongazing.OrionLock.sln

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        dotnet-version: ['8.0.x', '9.0.x', '10.0.x']
    steps:
      - name: Checkout
        uses: actions/checkout@v4
      - name: Setup .NET ${{ matrix.dotnet-version }}
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ matrix.dotnet-version }}
      - name: Restore
        run: dotnet restore ${{ env.SOLUTION_PATH }}
      - name: Build
        run: dotnet build ${{ env.SOLUTION_PATH }} --no-restore --configuration Release
      - name: Test
        run: dotnet test ${{ env.SOLUTION_PATH }} --no-restore --configuration Release --verbosity normal

  publish:
    needs: build-and-test
    runs-on: ubuntu-latest
    if: github.event_name == 'release'
    permissions:
      packages: write
      contents: read
    steps:
      - name: Checkout
        uses: actions/checkout@v4
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Restore
        run: dotnet restore ${{ env.SOLUTION_PATH }}
      - name: Build
        run: dotnet build ${{ env.SOLUTION_PATH }} --no-restore --configuration Release
      - name: Pack Packages
        run: |
          dotnet pack src/Moongazing.OrionLock/Moongazing.OrionLock.csproj --no-build --configuration Release -o ./nupkgs
          dotnet pack src/Moongazing.OrionLock.Redis/Moongazing.OrionLock.Redis.csproj --no-build --configuration Release -o ./nupkgs
          dotnet pack src/Moongazing.OrionLock.EntityFrameworkCore/Moongazing.OrionLock.EntityFrameworkCore.csproj --no-build --configuration Release -o ./nupkgs
          dotnet pack src/Moongazing.OrionLock.Testing/Moongazing.OrionLock.Testing.csproj --no-build --configuration Release -o ./nupkgs
      - name: Push to NuGet.org
        run: dotnet nuget push "./nupkgs/*.nupkg" --api-key "${{ secrets.NUGET }}" --source https://api.nuget.org/v3/index.json --skip-duplicate
      - name: Push to GitHub Packages
        run: |
          dotnet nuget add source --username ${{ github.repository_owner }} --password ${{ secrets.GITHUB_TOKEN }} --store-password-in-clear-text --name github "https://nuget.pkg.github.com/${{ github.repository_owner }}/index.json"
          dotnet nuget push "./nupkgs/*.nupkg" --source github --skip-duplicate
```

- [ ] **Step 2: Add per-package READMEs and packaging metadata**

For each of the four packable projects, create `docs/README.md` inside the project (a short, package-focused readme) and add to the csproj `<PropertyGroup>`:

```xml
<PackageReadmeFile>docs/README.md</PackageReadmeFile>
```

and to an `<ItemGroup>`:

```xml
<None Include="docs/README.md" Pack="true" PackagePath="docs/" />
```

- [ ] **Step 3: Full build and test**

Run: `dotnet build -c Release` then `dotnet test -c Release`
Expected: build warning-clean; all four test projects green. (The Redis test project needs Docker; if Docker is unavailable locally, note it — CI provides it.)

- [ ] **Step 4: Pack all four packages**

Run: `dotnet pack -c Release -o ./artifacts`
Expected: `OrionLock`, `OrionLock.Redis`, `OrionLock.EntityFrameworkCore`, `OrionLock.Testing` `.nupkg` files produced.

- [ ] **Step 5: Commit**

```
git add .github src/Moongazing.OrionLock src/Moongazing.OrionLock.Redis src/Moongazing.OrionLock.EntityFrameworkCore src/Moongazing.OrionLock.Testing
git commit -m "ci(orionlock): GitHub Actions pipeline and per-package READMEs"
```

---

## Final verification

- [ ] `dotnet build -c Release` — clean, zero warnings.
- [ ] `dotnet test -c Release` — all four test projects green (Redis tests require Docker).
- [ ] `dotnet pack -c Release -o ./artifacts` — four `.nupkg` files.
- [ ] `dotnet run --project sample/Moongazing.OrionLock.Sample` — prints the sample output.
- [ ] `git log --oneline` — one commit per task, in order.

---

## Self-Review

**Spec coverage:**

| Spec section | Task(s) |
|---|---|
| §3 solution & 4-package layout | Task 1 |
| §4 core abstraction (`IDistributedLock`, handle, options) | Task 2 |
| §4.1 exceptions | Task 2 |
| §5 `IDistributedLockProvider` primitive | Task 2 |
| §6.1 blocking acquire / retry | Task 5 |
| §6.2 lease auto-renewal watchdog | Task 4 |
| §6.3 reentrancy | Task 6 |
| §6.4 DI (`AddOrionLock`, builder) | Task 7 |
| §7 `OrionLock.Redis` | Task 9 |
| §8 `OrionLock.EntityFrameworkCore` | Tasks 10, 11 |
| §9 `OrionLock.Testing` | Tasks 3, 8 |
| §10 OpenTelemetry | Task 12 |
| §11 versioning / CI/CD | Tasks 1, 16 |
| §12 testing strategy | Tasks 3-12 (unit/integration), Task 13 (bench) |
| §13 documentation | Tasks 15, 16 |
| §14 / §15 downstream / out-of-scope | not tasks — correctly excluded |

Every in-scope spec section maps to a task. §9's spec text mentions a separate `InMemoryDistributedLock` convenience type in addition to `InMemoryLockProvider`; the plan ships `InMemoryLockProvider` + `UseInMemory()`, which fully delivers in-memory locking through the standard `DistributedLock` composition — a separate hand-written `InMemoryDistributedLock` would be redundant (it would just be `new DistributedLock(new InMemoryLockProvider())`), so it is intentionally omitted per YAGNI. This is the one deliberate deviation from the spec letter; it does not reduce capability.

**Placeholder scan:** No `TBD`/`TODO`. The `_Placeholder.cs` files are a real scaffolding step (Task 1 Step 6), deleted in Tasks 2/3/9/10.

**Type consistency:** `IDistributedLockProvider` (`TryAcquireAsync`/`TryRenewAsync`/`ReleaseAsync` with `string key, string ownerToken, TimeSpan leaseDuration, CancellationToken`) is defined in Task 2 and implemented identically in Tasks 3, 9, 11. `DistributedLockHandle` (Task 4) constructor `(IDistributedLockProvider, string, string, DistributedLockOptions)` is used unchanged in Task 5. `OrionLockBuilder.Services` (Task 7) is used by `UseInMemory`/`UseRedis`/`UseEntityFrameworkCore` in Tasks 8/9/11. `OrionLockDiagnostics.ActivitySourceName` (Task 12) matches the test. The handle field `lostCts` must be plain ASCII everywhere (Task 4 note).
