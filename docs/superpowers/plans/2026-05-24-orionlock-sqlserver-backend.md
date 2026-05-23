# OrionLock.SqlServer backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a new `OrionLock.SqlServer` backend package that implements `IDistributedLockProvider` on top of SQL Server's native `sp_getapplock` primitive, with session-scope lifetime (connection = lease).

**Architecture:** A `SqlServerLockProvider` singleton holds one dedicated `SqlConnection` per active lock, keyed by `ownerToken` in a `ConcurrentDictionary`. Acquire opens a connection and calls `sp_getapplock @LockOwner='Session' @LockTimeout=0`; renew runs `SELECT 1` over the held connection (a failure = SQL session is gone = lost lease); release calls `sp_releaseapplock` then disposes the connection. No EF Core dependency.

**Tech Stack:** .NET 8/9/10 multi-target, `Microsoft.Data.SqlClient` 5.2.x, `Testcontainers.MsSql` 3.10.x, xUnit 2.9.x. Spec: [docs/superpowers/specs/2026-05-24-orionlock-sqlserver-backend-design.md](../specs/2026-05-24-orionlock-sqlserver-backend-design.md). Branch: `feat/sqlserver-backend`.

---

## File map

**Create (production):**

- `src/Moongazing.OrionLock.SqlServer/Moongazing.OrionLock.SqlServer.csproj` — NuGet package metadata, references, pack rules.
- `src/Moongazing.OrionLock.SqlServer/SqlServerLockOptions.cs` — `KeyPrefix`, `CommandTimeout` options class.
- `src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs` — `IDistributedLockProvider, IDisposable` impl. Holds the session registry.
- `src/Moongazing.OrionLock.SqlServer/OrionLockSqlServerBuilderExtensions.cs` — `UseSqlServer(...)` DI extension.
- `src/Moongazing.OrionLock.SqlServer/docs/README.md` — NuGet package readme (PackageReadmeFile).

**Create (tests):**

- `tests/Moongazing.OrionLock.SqlServer.Tests/Moongazing.OrionLock.SqlServer.Tests.csproj` — test SDK + Testcontainers.MsSql + project refs.
- `tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs` — `IAsyncLifetime` container fixture + provider behaviour tests.
- `tests/Moongazing.OrionLock.SqlServer.Tests/SmokeTest.cs` — end-to-end through `AddOrionLock().UseSqlServer(...)`.

**Modify (existing):**

- `Moongazing.OrionLock.sln` — add both projects.
- `src/Moongazing.OrionLock/LockExceptions.cs` — add `OrionLockBackendException` class.
- `tests/Moongazing.OrionLock.Tests/` — small unit test for the new exception (one file added).
- `README.md` — add SqlServer to the Backends section bullet list.
- `CHANGELOG.md` — under `[Unreleased]` Added (create the section if missing).
- `docs/lease-and-renewal.md` — short paragraph noting SqlServer's connection-scoped lease semantics.
- `src/Moongazing.OrionLock.SqlServer/docs/logo.png` — copy from existing package's docs (same icon).

**Pack-time only (not committed as content):**

- Logo file is copied from another package's docs into the new package's docs before pack. Same pattern as Redis/EF Core.

---

## Task 1: Scaffold project, test project, and solution wiring

**Files:**

- Create: `src/Moongazing.OrionLock.SqlServer/Moongazing.OrionLock.SqlServer.csproj`
- Create: `src/Moongazing.OrionLock.SqlServer/Placeholder.cs` (deleted in Task 3)
- Create: `tests/Moongazing.OrionLock.SqlServer.Tests/Moongazing.OrionLock.SqlServer.Tests.csproj`
- Create: `tests/Moongazing.OrionLock.SqlServer.Tests/SmokeTest.cs`
- Modify: `Moongazing.OrionLock.sln`

- [ ] **Step 1: Create minimal production csproj**

`src/Moongazing.OrionLock.SqlServer/Moongazing.OrionLock.SqlServer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>OrionLock.SqlServer</PackageId>
    <Description>SQL Server (sp_getapplock) backend for OrionLock distributed locking.</Description>
    <PackageTags>distributed-lock;sql-server;sp_getapplock;applock;orionlock</PackageTags>
    <PackageReadmeFile>docs/README.md</PackageReadmeFile>
    <PackageIcon>docs/logo.png</PackageIcon>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" Version="5.2.2" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Moongazing.OrionLock\Moongazing.OrionLock.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Moongazing.OrionLock.SqlServer.Tests" />
  </ItemGroup>
</Project>
```

`InternalsVisibleTo` is added now (used in later tasks for test-only accessors). The `<None Include="docs/...">` pack rules are added in Task 11 when the readme exists.

- [ ] **Step 2: Add a temporary placeholder so the project builds**

`src/Moongazing.OrionLock.SqlServer/Placeholder.cs`:

```csharp
namespace Moongazing.OrionLock.SqlServer;

internal static class Placeholder { }
```

This is deleted in Task 3 when the real types arrive.

- [ ] **Step 3: Create test csproj**

`tests/Moongazing.OrionLock.SqlServer.Tests/Moongazing.OrionLock.SqlServer.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <TargetFramework>net10.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>
    <NoWarn>$(NoWarn);CA1707</NoWarn>
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
  <ItemGroup>
    <PackageReference Include="Testcontainers.MsSql" Version="3.10.0" />
    <PackageReference Include="Microsoft.Data.SqlClient" Version="5.2.2" />
    <ProjectReference Include="..\..\src\Moongazing.OrionLock\Moongazing.OrionLock.csproj" />
    <ProjectReference Include="..\..\src\Moongazing.OrionLock.SqlServer\Moongazing.OrionLock.SqlServer.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Add a build-only smoke test**

`tests/Moongazing.OrionLock.SqlServer.Tests/SmokeTest.cs`:

```csharp
namespace Moongazing.OrionLock.SqlServer.Tests;

public class SmokeTest
{
    [Fact]
    public void SolutionBuilds() => Assert.True(true);
}
```

(Mirrors the existing pattern in `tests/Moongazing.OrionLock.Redis.Tests/SmokeTest.cs`. We replace this with a real E2E test in Task 10.)

- [ ] **Step 5: Register both projects in the solution**

Run:

```bash
dotnet sln Moongazing.OrionLock.sln add src/Moongazing.OrionLock.SqlServer/Moongazing.OrionLock.SqlServer.csproj
dotnet sln Moongazing.OrionLock.sln add tests/Moongazing.OrionLock.SqlServer.Tests/Moongazing.OrionLock.SqlServer.Tests.csproj
```

- [ ] **Step 6: Build the solution**

Run:

```bash
dotnet build Moongazing.OrionLock.sln
```

Expected: build succeeds, all 4 existing + 2 new projects built.

- [ ] **Step 7: Run the smoke test**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests/Moongazing.OrionLock.SqlServer.Tests.csproj --filter "FullyQualifiedName~SmokeTest"
```

Expected: 1 test passed (`SolutionBuilds`).

- [ ] **Step 8: Commit**

```bash
git add Moongazing.OrionLock.sln src/Moongazing.OrionLock.SqlServer tests/Moongazing.OrionLock.SqlServer.Tests
git commit -m "chore(orionlock): scaffold OrionLock.SqlServer project and tests"
```

---

## Task 2: Add `OrionLockBackendException` to core

The provider needs an exception type to surface non-contention backend failures (e.g. `sp_getapplock` rc < -1) without conflating them with `LockAcquisitionTimeoutException`. The current `LockExceptions.cs` only has `LockAcquisitionTimeoutException` and `LeaseLostException`.

**Files:**

- Modify: `src/Moongazing.OrionLock/LockExceptions.cs`
- Create: `tests/Moongazing.OrionLock.Tests/OrionLockBackendExceptionTests.cs`

- [ ] **Step 1: Write failing test**

`tests/Moongazing.OrionLock.Tests/OrionLockBackendExceptionTests.cs`:

```csharp
namespace Moongazing.OrionLock.Tests;

public class OrionLockBackendExceptionTests
{
    [Fact]
    public void Constructor_ShouldCarryKeyAndInner()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new OrionLockBackendException("k1", "sp_getapplock returned -3", inner);

        Assert.Equal("k1", ex.Key);
        Assert.Same(inner, ex.InnerException);
        Assert.Contains("k1", ex.Message);
        Assert.Contains("sp_getapplock returned -3", ex.Message);
    }

    [Fact]
    public void Constructor_ShouldAllowNullInner()
    {
        var ex = new OrionLockBackendException("k2", "validation");
        Assert.Equal("k2", ex.Key);
        Assert.Null(ex.InnerException);
    }
}
```

- [ ] **Step 2: Run the test, confirm it fails to compile**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.Tests --filter "FullyQualifiedName~OrionLockBackendExceptionTests"
```

Expected: compile error, `OrionLockBackendException` not found.

- [ ] **Step 3: Add the exception class**

Append to `src/Moongazing.OrionLock/LockExceptions.cs`:

```csharp
/// <summary>
/// Thrown when a backend reports a non-contention failure during a lock operation
/// (e.g. SQL Server <c>sp_getapplock</c> deadlock victim, parameter validation, or any
/// other condition that is not "the lock is held by someone else").
/// </summary>
public sealed class OrionLockBackendException : Exception
{
    /// <summary>Initializes the exception with a key and a backend-specific reason.</summary>
    public OrionLockBackendException(string key, string reason)
        : base($"OrionLock backend failure for key '{key}': {reason}")
    {
        Key = key;
    }

    /// <summary>Initializes the exception with a key, reason, and inner exception.</summary>
    public OrionLockBackendException(string key, string reason, Exception inner)
        : base($"OrionLock backend failure for key '{key}': {reason}", inner)
    {
        Key = key;
    }

    /// <summary>The lock key.</summary>
    public string Key { get; }
}
```

- [ ] **Step 4: Run the tests, confirm they pass**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.Tests --filter "FullyQualifiedName~OrionLockBackendExceptionTests"
```

Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionLock/LockExceptions.cs tests/Moongazing.OrionLock.Tests/OrionLockBackendExceptionTests.cs
git commit -m "feat(orionlock): add OrionLockBackendException for non-contention backend failures"
```

---

## Task 3: `SqlServerLockOptions` with defaults

**Files:**

- Create: `src/Moongazing.OrionLock.SqlServer/SqlServerLockOptions.cs`
- Create: `tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockOptionsTests.cs`
- Delete: `src/Moongazing.OrionLock.SqlServer/Placeholder.cs`

- [ ] **Step 1: Write failing test**

`tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockOptionsTests.cs`:

```csharp
namespace Moongazing.OrionLock.SqlServer.Tests;

public class SqlServerLockOptionsTests
{
    [Fact]
    public void Defaults_ShouldMatchSpec()
    {
        var o = new SqlServerLockOptions();
        Assert.Equal(string.Empty, o.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(30), o.CommandTimeout);
    }

    [Fact]
    public void Setters_ShouldRoundTrip()
    {
        var o = new SqlServerLockOptions { KeyPrefix = "app:", CommandTimeout = TimeSpan.FromSeconds(5) };
        Assert.Equal("app:", o.KeyPrefix);
        Assert.Equal(TimeSpan.FromSeconds(5), o.CommandTimeout);
    }
}
```

- [ ] **Step 2: Run test, confirm compile failure**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~SqlServerLockOptionsTests"
```

Expected: compile error, `SqlServerLockOptions` not found.

- [ ] **Step 3: Implement the options class**

`src/Moongazing.OrionLock.SqlServer/SqlServerLockOptions.cs`:

```csharp
namespace Moongazing.OrionLock.SqlServer;

/// <summary>Configuration for the SQL Server <c>sp_getapplock</c> OrionLock backend.</summary>
public sealed class SqlServerLockOptions
{
    /// <summary>
    /// Prefix prepended to every lock key. Default empty. The combined length of
    /// <see cref="KeyPrefix"/> and the supplied key must not exceed 240 characters
    /// (SQL Server <c>sp_getapplock</c> @Resource is <c>nvarchar(255)</c>; the
    /// remaining 15 characters are a safety margin).
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Timeout applied to every command (<c>sp_getapplock</c>, <c>SELECT 1</c>,
    /// <c>sp_releaseapplock</c>). Bounds network hangs, not lock contention —
    /// contention is handled by the OrionLock retry loop above the provider.
    /// Default 30 seconds.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

- [ ] **Step 4: Delete the placeholder**

Delete `src/Moongazing.OrionLock.SqlServer/Placeholder.cs`.

- [ ] **Step 5: Run test, confirm pass**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~SqlServerLockOptionsTests"
```

Expected: 2 tests passed.

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionLock.SqlServer/SqlServerLockOptions.cs \
        tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockOptionsTests.cs
git rm src/Moongazing.OrionLock.SqlServer/Placeholder.cs
git commit -m "feat(orionlock): SqlServerLockOptions (KeyPrefix, CommandTimeout)"
```

---

## Task 4: Provider skeleton + key validation (no DB call yet)

We start with the cheap, no-network path: argument validation. This locks in the constructor signature and lets later tasks add network behaviour without re-deciding the shape.

**Files:**

- Create: `src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs`
- Create: `tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs`

- [ ] **Step 1: Write the failing test (no Testcontainers — pure validation)**

`tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs`:

```csharp
using Moongazing.OrionLock.SqlServer;

namespace Moongazing.OrionLock.SqlServer.Tests;

public partial class SqlServerLockProviderTests
{
    // Validation tests do not need a real SQL Server.
    private static SqlServerLockProvider NewProviderWithoutServer(string prefix = "")
        => new("Server=does-not-matter;", new SqlServerLockOptions { KeyPrefix = prefix });

    [Fact]
    public async Task TryAcquire_ShouldThrow_WhenKeyIsEmpty()
    {
        var p = NewProviderWithoutServer();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            p.TryAcquireAsync("", "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldThrow_WhenKeyIsWhitespace()
    {
        var p = NewProviderWithoutServer();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            p.TryAcquireAsync("   ", "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldThrow_WhenCombinedKeyExceeds240Chars()
    {
        var p = NewProviderWithoutServer(prefix: "app:");
        var longKey = new string('x', 237); // 4 (prefix) + 237 = 241
        await Assert.ThrowsAsync<ArgumentException>(() =>
            p.TryAcquireAsync(longKey, "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldNotThrow_WhenCombinedKeyIsAtBoundary()
    {
        // 240 chars combined is allowed (we throw at > 240). No DB call is made
        // because the connection string is bogus, so we expect a *connect* failure,
        // not an ArgumentException.
        var p = NewProviderWithoutServer(prefix: "app:");
        var key = new string('x', 236); // 4 + 236 = 240

        await Assert.ThrowsAnyAsync<Exception>(() =>
            p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
        // ^ either SqlException or InvalidOperationException — we only assert it's NOT ArgumentException.
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(async () =>
            await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default))
            .ContinueWith(_ => Task.CompletedTask); // tolerant: we just need "not ArgumentException".
    }
}
```

*Note on the last test:* Asserting "not `ArgumentException`" cleanly in xUnit is awkward; a simpler form is:

```csharp
    [Fact]
    public async Task TryAcquire_ShouldNotThrowArgumentException_AtBoundary()
    {
        var p = NewProviderWithoutServer(prefix: "app:");
        var key = new string('x', 236); // 4 + 236 = 240 (allowed)

        try
        {
            await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        }
        catch (ArgumentException)
        {
            Assert.Fail("Boundary key should not throw ArgumentException.");
        }
        catch
        {
            // SqlException or similar from the bogus connection string is fine.
        }
    }
```

Use the simpler form. Replace the last `[Fact]` in the file with this version.

- [ ] **Step 2: Run, confirm compile failure**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~SqlServerLockProviderTests"
```

Expected: compile error, `SqlServerLockProvider` not found.

- [ ] **Step 3: Implement the provider skeleton**

`src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs`:

```csharp
using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.SqlServer;

/// <summary>
/// SQL Server <c>sp_getapplock</c> backed <see cref="IDistributedLockProvider"/>. Holds one
/// dedicated <see cref="SqlConnection"/> per active lock — the lock lifetime IS the SQL session
/// lifetime, so a crashed process releases its locks automatically.
/// </summary>
public sealed class SqlServerLockProvider : IDistributedLockProvider, IDisposable
{
    private const int MaxResourceLength = 240;

    private readonly string connectionString;
    private readonly SqlServerLockOptions options;
    private readonly ConcurrentDictionary<string, SqlConnection> sessions = new();

    /// <summary>Creates the provider.</summary>
    public SqlServerLockProvider(string connectionString, SqlServerLockOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        this.connectionString = connectionString;
        this.options = options;
    }

    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        throw new NotImplementedException("TryAcquireAsync — implemented in Task 5.");
    }

    /// <inheritdoc />
    public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => throw new NotImplementedException("TryRenewAsync — implemented in Task 6.");

    /// <inheritdoc />
    public Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
        => throw new NotImplementedException("ReleaseAsync — implemented in Task 8.");

    /// <inheritdoc />
    public void Dispose() { /* implemented in Task 9 */ }

    private void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var total = options.KeyPrefix.Length + key.Length;
        if (total > MaxResourceLength)
        {
            throw new ArgumentException(
                $"Lock key (with prefix) is {total} characters; SQL Server sp_getapplock @Resource " +
                $"is limited to ~{MaxResourceLength} characters. Hash long keys on the caller side or " +
                "shorten the prefix.", nameof(key));
        }
    }

    // Test-only accessor used by SqlServerLockProviderTests (InternalsVisibleTo).
    internal SqlConnection? GetSessionForTesting(string ownerToken)
        => sessions.TryGetValue(ownerToken, out var c) ? c : null;
}
```

- [ ] **Step 4: Run, confirm validation tests pass**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~SqlServerLockProviderTests"
```

Expected: 4 validation tests passed. (Boundary test passes because we throw `NotImplementedException`, which is caught by the test's broad `catch`.)

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs \
        tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs
git commit -m "feat(orionlock): SqlServerLockProvider skeleton with key validation"
```

---

## Task 5: `TryAcquireAsync` — single-shot acquire (with `Testcontainers.MsSql`)

This is where the container fixture and real `sp_getapplock` call land.

**Files:**

- Create: `tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerContainerFixture.cs`
- Modify: `tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs`
- Modify: `src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs`

- [ ] **Step 1: Add the container fixture (class fixture, shared across tests in the class)**

`tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerContainerFixture.cs`:

```csharp
using Testcontainers.MsSql;

namespace Moongazing.OrionLock.SqlServer.Tests;

public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder().Build();

    public string ConnectionString { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
```

- [ ] **Step 2: Write the failing acquire tests**

Add to `tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs` (top of the file, before the `partial class` you already have — convert it to use the fixture; the existing validation tests still work because they construct a provider with a fake connection string):

```csharp
using Moongazing.OrionLock.SqlServer;

namespace Moongazing.OrionLock.SqlServer.Tests;

public partial class SqlServerLockProviderTests : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture fx;

    public SqlServerLockProviderTests(SqlServerContainerFixture fx) => this.fx = fx;

    private SqlServerLockProvider NewProvider()
        => new(fx.ConnectionString, new SqlServerLockOptions());

    [Fact]
    public async Task TryAcquire_ShouldSucceedThenBlockSecondOwner()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        Assert.True(await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
        Assert.False(await p.TryAcquireAsync(key, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        var tasks = Enumerable.Range(0, 5)
            .Select(i => p.TryAcquireAsync(key, $"owner-{i}", TimeSpan.FromSeconds(30), default))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
    }
}
```

(Note: the existing validation tests in this `partial class` keep working because they call `NewProviderWithoutServer`, not `NewProvider`.)

- [ ] **Step 3: Run, confirm tests fail**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~TryAcquire_ShouldSucceedThenBlockSecondOwner|FullyQualifiedName~TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers"
```

Expected: 2 failures, `NotImplementedException` from `TryAcquireAsync`.

- [ ] **Step 4: Implement `TryAcquireAsync`**

Replace the `TryAcquireAsync` body in `src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs`:

```csharp
public async Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
{
    ValidateKey(key);
    ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

    var conn = new SqlConnection(connectionString);
    try
    {
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        int returnCode;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandText = "sp_getapplock";
            cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;

            cmd.Parameters.Add(new SqlParameter("@Resource",    SqlDbType.NVarChar, 255) { Value = options.KeyPrefix + key });
            cmd.Parameters.Add(new SqlParameter("@LockMode",    SqlDbType.VarChar,  32)  { Value = "Exclusive" });
            cmd.Parameters.Add(new SqlParameter("@LockOwner",   SqlDbType.VarChar,  32)  { Value = "Session" });
            cmd.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int)           { Value = 0 });
            cmd.Parameters.Add(new SqlParameter("@DbPrincipal", SqlDbType.NVarChar, 32)  { Value = "public" });

            var rc = new SqlParameter
            {
                ParameterName = "@RC",
                SqlDbType = SqlDbType.Int,
                Direction = ParameterDirection.ReturnValue
            };
            cmd.Parameters.Add(rc);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            returnCode = (int)rc.Value!;
        }

        switch (returnCode)
        {
            case >= 0:
                if (!sessions.TryAdd(ownerToken, conn))
                {
                    // ownerToken collision — vanishingly unlikely with GUIDs, but defensive.
                    await ReleaseInSession(conn, options.KeyPrefix + key, cancellationToken).ConfigureAwait(false);
                    await conn.DisposeAsync().ConfigureAwait(false);
                    throw new InvalidOperationException($"ownerToken '{ownerToken}' already registered.");
                }
                return true;

            case -1:
                await conn.DisposeAsync().ConfigureAwait(false);
                return false;

            case -2:
                await conn.DisposeAsync().ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);

            default:
                await conn.DisposeAsync().ConfigureAwait(false);
                throw new OrionLockBackendException(
                    key, $"sp_getapplock returned {returnCode} (deadlock victim, validation error, or other backend failure).");
        }
    }
    catch
    {
        try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* already failing */ }
        throw;
    }
}

// Helper used by the collision branch above and by ReleaseAsync (Task 8).
private async Task ReleaseInSession(SqlConnection conn, string resource, CancellationToken ct)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandType = CommandType.StoredProcedure;
    cmd.CommandText = "sp_releaseapplock";
    cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
    cmd.Parameters.Add(new SqlParameter("@Resource",    SqlDbType.NVarChar, 255) { Value = resource });
    cmd.Parameters.Add(new SqlParameter("@LockOwner",   SqlDbType.VarChar,  32)  { Value = "Session" });
    cmd.Parameters.Add(new SqlParameter("@DbPrincipal", SqlDbType.NVarChar, 32)  { Value = "public" });
    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
}
```

Required `using` additions at top of file (if not already present): `using Moongazing.OrionLock;` (for `OrionLockBackendException`).

- [ ] **Step 5: Run, confirm acquire tests pass**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~TryAcquire_ShouldSucceedThenBlockSecondOwner|FullyQualifiedName~TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers"
```

Expected: 2 tests passed. (First test may take ~30 s on first run for the MsSql container to pull and start.)

- [ ] **Step 6: Commit**

```bash
git add src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs \
        tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerContainerFixture.cs \
        tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs
git commit -m "feat(orionlock): SqlServerLockProvider.TryAcquireAsync via sp_getapplock"
```

---

## Task 6: `TryRenewAsync` — owner-known SELECT 1, unknown returns false

**Files:**

- Modify: `tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs`
- Modify: `src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs`

- [ ] **Step 1: Add the failing tests**

Append to `SqlServerLockProviderTests.cs` (inside the same partial class):

```csharp
    [Fact]
    public async Task TryRenew_ShouldReturnTrue_ForKnownOwner()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        Assert.True(await p.TryRenewAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task TryRenew_ShouldReturnFalse_ForUnknownOwner()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        Assert.False(await p.TryRenewAsync(key, "owner-2", TimeSpan.FromSeconds(30), default));
    }
```

- [ ] **Step 2: Run, confirm failures**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~TryRenew_ShouldReturnTrue_ForKnownOwner|FullyQualifiedName~TryRenew_ShouldReturnFalse_ForUnknownOwner"
```

Expected: 2 failures, `NotImplementedException`.

- [ ] **Step 3: Implement `TryRenewAsync`**

Replace the `TryRenewAsync` body in `SqlServerLockProvider.cs`:

```csharp
public async Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

    if (!sessions.TryGetValue(ownerToken, out var conn))
    {
        return false;
    }

    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
        await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
    catch
    {
        // Connection is no longer trustworthy — SQL Server has released the session
        // (and therefore the lock) or the link is broken. Forget the session.
        sessions.TryRemove(ownerToken, out _);
        try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* already dead */ }
        return false;
    }
}
```

- [ ] **Step 4: Run, confirm tests pass**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~TryRenew_ShouldReturnTrue_ForKnownOwner|FullyQualifiedName~TryRenew_ShouldReturnFalse_ForUnknownOwner"
```

Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs \
        tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs
git commit -m "feat(orionlock): SqlServerLockProvider.TryRenewAsync (SELECT 1 health check)"
```

---

## Task 7: `TryRenewAsync` — connection-drop returns false (KILL @@SPID)

The lost-lease story is the SqlServer-specific selling point. This test verifies it.

**Files:**

- Modify: `tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs`

- [ ] **Step 1: Add the failing test**

Append to `SqlServerLockProviderTests.cs`:

```csharp
    [Fact]
    public async Task TryRenew_ShouldReturnFalse_AfterConnectionKilled()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);

        // Read the SPID of the session that holds the lock.
        var heldConn = p.GetSessionForTesting("owner-1")!;
        Assert.NotNull(heldConn);
        short spid;
        using (var cmd = heldConn.CreateCommand())
        {
            cmd.CommandText = "SELECT @@SPID";
            spid = (short)(await cmd.ExecuteScalarAsync())!;
        }

        // From a side connection, kill that session.
        await using (var killConn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await killConn.OpenAsync();
            using var kill = killConn.CreateCommand();
            kill.CommandText = $"KILL {spid}";
            await kill.ExecuteNonQueryAsync();
        }

        // Next renewal must observe the broken session and return false.
        Assert.False(await p.TryRenewAsync(key, "owner-1", TimeSpan.FromSeconds(30), default));
    }
```

- [ ] **Step 2: Run, confirm test passes**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~TryRenew_ShouldReturnFalse_AfterConnectionKilled"
```

Expected: PASS — the existing `TryRenewAsync` implementation already handles broken connections through its broad `catch`. No production change needed.

If the test fails, debug: the SELECT 1 should throw a `SqlException` (severity 20+ or "connection closed"); the `catch` should remove the session and return false. If renewal returns `true`, the `catch` is too narrow or the session was never registered — fix the production code, not the test.

- [ ] **Step 3: Commit**

```bash
git add tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs
git commit -m "test(orionlock): SqlServerLockProvider renewal returns false after KILL @@SPID"
```

---

## Task 8: `ReleaseAsync` — owner-known release, unknown is no-op

**Files:**

- Modify: `tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs`
- Modify: `src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs`

- [ ] **Step 1: Add the failing tests**

Append to `SqlServerLockProviderTests.cs`:

```csharp
    [Fact]
    public async Task Release_ShouldAllowNextAcquire_ForOwner()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync(key, "owner-1", default);

        Assert.True(await p.TryAcquireAsync(key, "owner-2", TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Release_ShouldBeNoOp_ForUnknownOwnerToken()
    {
        using var p = NewProvider();
        var key = $"k-{Guid.NewGuid():N}";

        // Acquire as owner-1, then call Release with an unrelated token. Must not throw
        // and must not release owner-1's lock.
        await p.TryAcquireAsync(key, "owner-1", TimeSpan.FromSeconds(30), default);
        await p.ReleaseAsync(key, "never-seen", default);

        Assert.False(await p.TryAcquireAsync(key, "owner-3", TimeSpan.FromSeconds(30), default));
    }
```

- [ ] **Step 2: Run, confirm failures**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~Release_ShouldAllowNextAcquire_ForOwner|FullyQualifiedName~Release_ShouldBeNoOp_ForUnknownOwnerToken"
```

Expected: 2 failures (`NotImplementedException` from the no-op release; the second test also fails because the first acquire's `ReleaseAsync` throws when the no-op release is called).

- [ ] **Step 3: Implement `ReleaseAsync`**

Replace the `ReleaseAsync` body in `SqlServerLockProvider.cs`:

```csharp
public async Task ReleaseAsync(string key, string ownerToken, CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(ownerToken);

    if (!sessions.TryRemove(ownerToken, out var conn))
    {
        // Unknown token (never acquired or already released) — no-op, mirrors Redis/EF Core.
        return;
    }

    try
    {
        await ReleaseInSession(conn, options.KeyPrefix + key, cancellationToken).ConfigureAwait(false);
    }
    catch
    {
        // Connection is dying; SQL Server releases session-scoped locks when the session
        // ends, so disposing the connection below still drops the lock.
    }
    finally
    {
        try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
    }
}
```

- [ ] **Step 4: Run, confirm tests pass**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~Release_ShouldAllowNextAcquire_ForOwner|FullyQualifiedName~Release_ShouldBeNoOp_ForUnknownOwnerToken"
```

Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs \
        tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs
git commit -m "feat(orionlock): SqlServerLockProvider.ReleaseAsync via sp_releaseapplock"
```

---

## Task 9: Provider `Dispose` releases all open sessions

**Files:**

- Modify: `tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs`
- Modify: `src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs`

- [ ] **Step 1: Add the failing test**

Append to `SqlServerLockProviderTests.cs`:

```csharp
    [Fact]
    public async Task Dispose_ShouldReleaseAllOpenSessions()
    {
        var p1 = NewProvider();
        var keys = Enumerable.Range(0, 3).Select(_ => $"k-{Guid.NewGuid():N}").ToArray();

        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(await p1.TryAcquireAsync(keys[i], $"owner-{i}", TimeSpan.FromSeconds(30), default));
        }

        p1.Dispose();

        // A fresh provider must be able to acquire all three keys (the previous sessions
        // are closed, so the locks are released).
        using var p2 = NewProvider();
        for (var i = 0; i < keys.Length; i++)
        {
            Assert.True(await p2.TryAcquireAsync(keys[i], $"owner-after-{i}", TimeSpan.FromSeconds(30), default));
        }
    }
```

- [ ] **Step 2: Run, confirm failure**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~Dispose_ShouldReleaseAllOpenSessions"
```

Expected: FAIL. The current `Dispose` is empty so the connections leak in the registry; the second provider's acquire on each key returns false because the original session still holds the lock via the pooled connection.

- [ ] **Step 3: Implement `Dispose`**

Replace the `Dispose` body in `SqlServerLockProvider.cs`:

```csharp
public void Dispose()
{
    foreach (var kv in sessions.ToArray())
    {
        if (sessions.TryRemove(kv.Key, out var conn))
        {
            try { conn.Dispose(); } catch { /* best effort */ }
        }
    }
}
```

(Synchronous dispose because `IDisposable` is sync. `SqlConnection.Dispose()` closes the connection, returning it to the pool with `sp_reset_connection`, which clears session-scoped applocks.)

- [ ] **Step 4: Run, confirm pass**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~Dispose_ShouldReleaseAllOpenSessions"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionLock.SqlServer/SqlServerLockProvider.cs \
        tests/Moongazing.OrionLock.SqlServer.Tests/SqlServerLockProviderTests.cs
git commit -m "feat(orionlock): SqlServerLockProvider.Dispose releases all open sessions"
```

---

## Task 10: `UseSqlServer` DI extension + real end-to-end SmokeTest

**Files:**

- Create: `src/Moongazing.OrionLock.SqlServer/OrionLockSqlServerBuilderExtensions.cs`
- Replace: `tests/Moongazing.OrionLock.SqlServer.Tests/SmokeTest.cs`

- [ ] **Step 1: Replace the build-only SmokeTest with an end-to-end one**

Overwrite `tests/Moongazing.OrionLock.SqlServer.Tests/SmokeTest.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionLock;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.SqlServer;

namespace Moongazing.OrionLock.SqlServer.Tests;

public sealed class SmokeTest : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture fx;

    public SmokeTest(SqlServerContainerFixture fx) => this.fx = fx;

    [Fact]
    public async Task AddOrionLock_UseSqlServer_AcquireDispose_Works()
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseSqlServer(fx.ConnectionString);
        await using var sp = services.BuildServiceProvider();

        var locker = sp.GetRequiredService<IDistributedLock>();
        var key = $"smoke-{Guid.NewGuid():N}";

        await using (var handle = await locker.AcquireAsync(key, TimeSpan.FromSeconds(30)))
        {
            Assert.True(handle.IsHeld);
        }

        // After dispose, another acquire must succeed.
        await using var second = await locker.AcquireAsync(key, TimeSpan.FromSeconds(30));
        Assert.True(second.IsHeld);
    }

    [Fact]
    public async Task TwoConsumers_AcquireSerialise()
    {
        var services = new ServiceCollection();
        services.AddOrionLock().UseSqlServer(fx.ConnectionString);
        await using var sp = services.BuildServiceProvider();
        var locker = sp.GetRequiredService<IDistributedLock>();
        var key = $"smoke-{Guid.NewGuid():N}";

        await using var first = await locker.AcquireAsync(key, TimeSpan.FromSeconds(30));

        // Try-acquire from the *same* IDistributedLock instance is intercepted by
        // in-process reentrancy and would return a nested handle. Build a *second*
        // service provider to simulate a separate consumer.
        var services2 = new ServiceCollection();
        services2.AddOrionLock().UseSqlServer(fx.ConnectionString);
        await using var sp2 = services2.BuildServiceProvider();
        var locker2 = sp2.GetRequiredService<IDistributedLock>();

        var blocked = await locker2.TryAcquireAsync(key, new DistributedLockOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(30)
        });
        Assert.Null(blocked);
    }
}
```

- [ ] **Step 2: Run, confirm compile failure**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~SmokeTest"
```

Expected: compile error, `UseSqlServer` not found.

- [ ] **Step 3: Implement the DI extension**

`src/Moongazing.OrionLock.SqlServer/OrionLockSqlServerBuilderExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moongazing.OrionLock.DependencyInjection;
using Moongazing.OrionLock.Providers;

namespace Moongazing.OrionLock.SqlServer;

/// <summary>Registers the SQL Server (<c>sp_getapplock</c>) OrionLock backend.</summary>
public static class OrionLockSqlServerBuilderExtensions
{
    /// <summary>
    /// Uses SQL Server <c>sp_getapplock</c> as the OrionLock backend. The provider opens a
    /// dedicated <see cref="Microsoft.Data.SqlClient.SqlConnection"/> per active lock and
    /// holds it for the lifetime of the handle.
    /// </summary>
    public static OrionLockBuilder UseSqlServer(
        this OrionLockBuilder builder,
        string connectionString,
        Action<SqlServerLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new SqlServerLockOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<IDistributedLockProvider>(
            _ => new SqlServerLockProvider(connectionString, options));

        return builder;
    }
}
```

- [ ] **Step 4: Run, confirm tests pass**

Run:

```bash
dotnet test tests/Moongazing.OrionLock.SqlServer.Tests --filter "FullyQualifiedName~SmokeTest"
```

Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionLock.SqlServer/OrionLockSqlServerBuilderExtensions.cs \
        tests/Moongazing.OrionLock.SqlServer.Tests/SmokeTest.cs
git commit -m "feat(orionlock): UseSqlServer DI extension + end-to-end smoke test"
```

---

## Task 11: NuGet package metadata (PackageReadme + logo + pack rules)

**Files:**

- Create: `src/Moongazing.OrionLock.SqlServer/docs/README.md`
- Copy: `src/Moongazing.OrionLock.SqlServer/docs/logo.png` (from any existing package's docs)
- Modify: `src/Moongazing.OrionLock.SqlServer/Moongazing.OrionLock.SqlServer.csproj`

- [ ] **Step 1: Create the package README**

`src/Moongazing.OrionLock.SqlServer/docs/README.md`:

```markdown
# OrionLock.SqlServer

SQL Server backend for [OrionLock](https://www.nuget.org/packages/OrionLock) using the
native `sp_getapplock` application lock primitive. Session-scope lifetime: the lock is
held only while the dedicated SQL session is alive, so a crashed process releases its
locks automatically (no clock-based expiry needed).

```csharp
services.AddOrionLock()
        .UseSqlServer("Server=...;Database=app;Trusted_Connection=true;");
```

### Notes

- **Case-insensitive keys.** `sp_getapplock @Resource` uses the server's default
  collation; on stock installs `"Invoice:42"` and `"invoice:42"` collide. This
  differs from Redis (case-sensitive). Use `KeyPrefix` to namespace, not casing.
- **240-character key limit.** Combined `KeyPrefix + key` must be ≤ 240
  characters; longer keys throw `ArgumentException`. Hash on the caller side.
- **Connection pooling.** Leave `Microsoft.Data.SqlClient` pooling at its
  default (enabled). The provider holds each session open for the lifetime of
  the lock and only returns it to the pool *after* calling
  `sp_releaseapplock`, so pool reset is harmless.

Requires the `OrionLock` package. See https://github.com/tunahanaliozturk/OrionLock.
```

- [ ] **Step 2: Copy the logo into the new package's docs folder**

Run:

```bash
cp src/Moongazing.OrionLock.Redis/docs/logo.png src/Moongazing.OrionLock.SqlServer/docs/logo.png
```

- [ ] **Step 3: Update the csproj to pack the docs**

Add this `<ItemGroup>` at the bottom of `src/Moongazing.OrionLock.SqlServer/Moongazing.OrionLock.SqlServer.csproj` (mirrors Redis/EF Core):

```xml
  <ItemGroup>
    <None Include="docs/README.md" Pack="true" PackagePath="docs/" />
    <None Include="docs/logo.png" Pack="true" PackagePath="docs/" />
  </ItemGroup>
```

- [ ] **Step 4: Verify the package builds and contains the README + logo**

Run:

```bash
dotnet pack src/Moongazing.OrionLock.SqlServer/Moongazing.OrionLock.SqlServer.csproj -c Release -o artifacts/local-pack
```

Then inspect the generated `.nupkg` (it's a zip):

```bash
unzip -l artifacts/local-pack/OrionLock.SqlServer.*.nupkg | grep -E "docs/(README\.md|logo\.png)"
```

Expected output: two lines, one for `docs/README.md` and one for `docs/logo.png`.

If `unzip` is not on the path (Windows), open the `.nupkg` with any zip tool and confirm the two files are under `docs/`.

- [ ] **Step 5: Commit**

```bash
git add src/Moongazing.OrionLock.SqlServer/docs/README.md \
        src/Moongazing.OrionLock.SqlServer/docs/logo.png \
        src/Moongazing.OrionLock.SqlServer/Moongazing.OrionLock.SqlServer.csproj
git commit -m "chore(orionlock): NuGet PackageReadme + logo + pack rules for OrionLock.SqlServer"
```

---

## Task 12: Repo-level docs (root README, CHANGELOG, lease guide)

**Files:**

- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/lease-and-renewal.md`

- [ ] **Step 1: Update root `README.md` — Backends section**

Find this block in `README.md`:

```markdown
## Backends

- **`OrionLock.Redis`** — `SET NX PX` acquire, owner-checked Lua renew/release. Single Redis endpoint (single-instance lock; multi-master RedLock is post-0.1).
- **`OrionLock.EntityFrameworkCore`** — provider-agnostic `OrionLock_Locks` table; PostgreSQL, SQL Server, MySQL, SQLite. See [docs/migrations/orionlock-locks-table.md](docs/migrations/orionlock-locks-table.md).
- **`OrionLock.Testing`** — in-memory provider for tests, no Redis or DB required.
```

Insert a new bullet between `OrionLock.EntityFrameworkCore` and `OrionLock.Testing`:

```markdown
- **`OrionLock.SqlServer`** — native `sp_getapplock` with session-scope lifetime. Crash-safe (no clock-based expiry; SQL Server releases the lock when the session ends) and faster than the EF Core lock table on SQL Server.
```

- [ ] **Step 2: Update `CHANGELOG.md`**

Insert a new `[Unreleased]` section at the top (after the intro paragraph, before `## [0.1.1]`):

```markdown
## [Unreleased]

### Added

- `OrionLock.SqlServer` backend using native `sp_getapplock` with session-scope
  lifetime. The lock is held while the SQL session is alive — a crashed process
  releases its locks automatically, with no clock-based expiry. `KeyPrefix` and
  `CommandTimeout` options; combined key length limit of 240 characters
  (SQL Server `@Resource` is `nvarchar(255)` with a 15-char safety margin).
- `OrionLockBackendException` for non-contention backend failures (e.g. SQL
  Server `sp_getapplock` deadlock-victim and validation errors), distinct from
  `LockAcquisitionTimeoutException`.
```

- [ ] **Step 3: Update `docs/lease-and-renewal.md`**

Append a new section at the end of the file:

```markdown
## A note on the SqlServer backend

`OrionLock.SqlServer` has the same `IDistributedLockHandle` contract as the
other backends — `IsHeld` flips and `LostToken` fires when the lease is lost —
but its underlying lease model is different. There is no clock-based expiry.
The lock is held while the SQL session that took it is alive, and `LeaseDuration`
only governs how often the watchdog runs its `SELECT 1` connection health check.

The practical effect: false positives from clock skew between application
nodes and SQL Server are impossible on this backend. The trade-off is that
each held lock costs one open SQL connection.
```

- [ ] **Step 4: Build and run the full test suite to make sure nothing broke**

Run:

```bash
dotnet build Moongazing.OrionLock.sln
dotnet test  Moongazing.OrionLock.sln
```

Expected: build succeeds, all tests pass (existing + new SqlServer tests).

- [ ] **Step 5: Commit**

```bash
git add README.md CHANGELOG.md docs/lease-and-renewal.md
git commit -m "docs(orionlock): README/CHANGELOG/lease-guide entries for OrionLock.SqlServer"
```

---

## Task 13: Final verification + open the PR

**Files:** none

- [ ] **Step 1: Run a clean build**

Run:

```bash
dotnet clean Moongazing.OrionLock.sln
dotnet build Moongazing.OrionLock.sln -c Release
```

Expected: 0 warnings, 0 errors. `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props` means any warning fails the build — investigate and fix anything that does.

- [ ] **Step 2: Run all tests in Release**

Run:

```bash
dotnet test Moongazing.OrionLock.sln -c Release
```

Expected: all tests pass across all four backend test projects.

- [ ] **Step 3: Verify NuGet packs for the new package**

Run:

```bash
dotnet pack src/Moongazing.OrionLock.SqlServer/Moongazing.OrionLock.SqlServer.csproj -c Release -o artifacts/local-pack
ls -la artifacts/local-pack/OrionLock.SqlServer.*.nupkg
```

Expected: one `OrionLock.SqlServer.0.1.1.nupkg` file (or whatever the current `<Version>` is; v0.2.0 bump happens in the release commit, not here).

- [ ] **Step 4: Push the branch and open the PR**

Run:

```bash
git push -u origin feat/sqlserver-backend
gh pr create --title "feat(orionlock): SqlServer backend using sp_getapplock" --body "$(cat <<'EOF'
## Summary

- New `OrionLock.SqlServer` backend that implements `IDistributedLockProvider`
  on top of SQL Server's native `sp_getapplock` primitive.
- Session-scope lifetime — the provider holds a dedicated `SqlConnection` per
  active lock, so a crashed process releases its locks automatically (no
  clock-based expiry).
- Renewal is a `SELECT 1` connection health check; a broken session trips
  `LostToken`.
- Added `OrionLockBackendException` for non-contention backend failures.
- First of four v0.2.0 work items (Postgres, RedLock, stress harness follow).
- Design spec: `docs/superpowers/specs/2026-05-24-orionlock-sqlserver-backend-design.md`.

## Test plan

- [x] Unit tests for `OrionLockBackendException`
- [x] Unit tests for `SqlServerLockOptions` defaults
- [x] Provider validation tests (key length, null/whitespace key)
- [x] `Testcontainers.MsSql` integration tests covering acquire, renew,
  release, dispose, parallel-acquire-exactly-one, and the `KILL @@SPID`
  lost-lease path
- [x] End-to-end `SmokeTest` through `AddOrionLock().UseSqlServer(...)`
- [x] `dotnet pack` produces a `.nupkg` containing `docs/README.md` and
  `docs/logo.png`

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL printed. Stop here — merging to `main` is a separate decision.

---

## Self-review (for the plan author, run once after writing)

**Spec coverage:**

- §1 Goal & scope → covered by Tasks 1–10 (production code) and Task 12 (docs).
- §1 Out-of-scope items (transaction-scope, factory overload, distributed-tx, auto-hash) → intentionally absent from all tasks. Correct.
- §2 Architecture (session registry, dependencies, data flow, why-this-structure, pooling) → Tasks 4–9 implement, Task 11 documents the pooling note.
- §3 Public API surface (`SqlServerLockOptions`, provider ctor, `UseSqlServer`) → Tasks 3, 4, 10.
- §3 Error semantics on the boundary → Task 5 (acquire errors) + Task 2 (`OrionLockBackendException`).
- §4 SQL call mechanics (acquire/renew/release T-SQL) → Tasks 5, 6, 8.
- §4 `@DbPrincipal = 'public'` rationale → Task 5 (the value is hard-coded; the rationale is in the spec).
- §4 Concurrency notes (watchdog/dispose race) → Task 6 (the broad `catch` covers it). The race is acknowledged in the spec but no explicit test — acceptable because the integration test in Task 7 exercises the same code path through a real session kill.
- §5 Lifecycle/error/lost-lease → exercised end-to-end in Task 10's SmokeTest and Task 7's KILL test.
- §6 Lock-key validation → Task 4.
- §6 Collation note → Task 11 (package README).
- §6 Test matrix (9 tests) → Tasks 4–10 cover all 9. Mapped:
  - `TryAcquire_ShouldSucceedThenBlockSecondOwner` → Task 5
  - `TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers` → Task 5
  - `TryRenew_ShouldExtendForOwner_AndRejectNonOwner` → Task 6
  - `Release_ShouldOnlyReleaseForOwner` → Task 8
  - `TryRenew_ShouldReturnFalse_AfterConnectionDrop` → Task 7
  - `Release_ShouldBeNoOp_ForUnknownOwnerToken` → Task 8
  - `KeyLengthLimit_ShouldThrowEarly` → Task 4
  - `Dispose_ShouldReleaseAllOpenSessions` → Task 9
  - `SmokeTest` end-to-end → Task 10
- §6 CI note (MsSql container cost) → not a code change; documented in the spec.
- §7 Documentation (PackageReadme, root README, CHANGELOG, lease-and-renewal, sample/bench exclusion, ROADMAP exclusion) → Tasks 11, 12.
- §7 Solution wiring → Task 1.
- §7 Branch/PR/release sequence → Task 13. The plan deliberately does NOT bump `<Version>` to 0.2.0; that happens in the dedicated v0.2.0 release commit after all four work items merge.

**Placeholder scan:** None of the forbidden patterns ("TBD", "appropriate error handling", "similar to Task N", "implement later", etc.) appear in any task body. Every code step has the full code.

**Type consistency:**

- `SqlServerLockProvider` constructor signature `(string connectionString, SqlServerLockOptions options)` is consistent across Tasks 4, 5, 10 and in `UseSqlServer`.
- `SqlServerLockOptions` properties `KeyPrefix` and `CommandTimeout` referenced identically in Tasks 3, 4, 5, 6, 8, 10.
- `OrionLockBackendException(string key, string reason)` and `(string key, string reason, Exception inner)` are the only two signatures used (Tasks 2, 5).
- `GetSessionForTesting(string)` (internal) defined in Task 4, used in Task 7. Names match.
- `ReleaseInSession(SqlConnection, string, CancellationToken)` (private helper) defined in Task 5, used in Task 8. Names match.

No issues found.
