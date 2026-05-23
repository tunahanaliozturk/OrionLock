# OrionLock.SqlServer backend — design

- Status: approved
- Date: 2026-05-24
- Target release: part of v0.2.0 (alongside Postgres, RedLock, stress harness)
- Branch: `feat/sqlserver-backend`

## 1. Goal & scope

Ship `OrionLock.SqlServer` — a new backend package that implements
`IDistributedLockProvider` on top of SQL Server's native `sp_getapplock`
application lock primitive.

The package exists *next to*, not as a replacement for, the existing
`OrionLock.EntityFrameworkCore` lock-table backend. It earns its place with two
properties the table-based backend cannot offer:

1. **Crash-safe by construction.** A lock is held only while the SQL session
   that took it is alive. If the application process dies, SQL Server tears the
   session down and releases the lock automatically. There is no clock-based
   expiry, so clock skew between application nodes and the database is a
   non-issue.
2. **Faster on the hot path.** No `UPDATE` against a lock table — SQL Server's
   internal lock manager does the work directly. The cost is bounded by a
   stored-procedure call plus connection lifetime.

### Out of scope for this spec

- Transaction-scope mode (`@LockOwner='Transaction'`). Considered for v0.3+.
- Factory-delegate connection source (`Func<SqlConnection>` overload). v0.3+ if
  there is demand.
- Distributed-transaction integration (OrionFlow territory).
- Automatic key hashing — long keys throw with a clear message; callers hash
  on their side if needed.

### Release model

This spec covers **one of the four** v0.2.0 work items. v0.2.0 is released only
after all four are on `main`:

1. **`OrionLock.SqlServer`** *(this spec)*
2. `OrionLock.Postgres`
3. Multi-master RedLock (`RedLockDistributedLock` next to `RedisLockProvider`)
4. Concurrency stress harness

When all four are merged, a single `chore(release): v0.2.0` commit bumps the
version, finalises the changelog, moves v0.2.0 to *Released* in the roadmap,
and the existing tag-push pipeline publishes the four packages to NuGet.

## 2. Architecture

### Project layout

```text
src/Moongazing.OrionLock.SqlServer/
  Moongazing.OrionLock.SqlServer.csproj
  SqlServerLockProvider.cs                ← IDistributedLockProvider impl
  SqlServerLockOptions.cs                 ← KeyPrefix, CommandTimeout
  OrionLockSqlServerBuilderExtensions.cs  ← UseSqlServer
  docs/
    PackageReadme.md

tests/Moongazing.OrionLock.SqlServer.Tests/
  SqlServerLockProviderTests.cs           ← Testcontainers.MsSql
  SmokeTest.cs                            ← end-to-end via DistributedLock
  Moongazing.OrionLock.SqlServer.Tests.csproj
```

### Dependencies

- `Microsoft.Data.SqlClient` (current LTS)
- `Moongazing.OrionLock` (project reference)
- **No EF Core dependency.** Standalone.

### Class model

`SqlServerLockProvider : IDistributedLockProvider, IDisposable` — registered as
singleton in DI.

```csharp
private readonly string connectionString;
private readonly SqlServerLockOptions options;
private readonly ConcurrentDictionary<string, SqlConnection> sessions; // key = ownerToken
```

`Dispose()` releases and disposes any remaining sessions. In normal flow the
dictionary is empty at process shutdown; this is defensive cleanup.

### Data flow

```text
TryAcquireAsync(key, ownerToken, lease, ct)
  ├─ ValidateKey(key)                        // length(prefix+key) <= 240
  ├─ conn = new SqlConnection(connectionString)
  ├─ await conn.OpenAsync(ct)
  ├─ rc = await sp_getapplock(@Resource=Prefix+key, @LockMode='Exclusive',
  │                            @LockOwner='Session', @LockTimeout=0,
  │                            @DbPrincipal='public')
  ├─ if rc >= 0:  sessions.TryAdd(ownerToken, conn); return true
  ├─ if rc == -1: await conn.DisposeAsync(); return false
  └─ if rc <  -1: await conn.DisposeAsync(); throw OrionLockBackendException

TryRenewAsync(key, ownerToken, lease, ct)
  ├─ if !sessions.TryGetValue(ownerToken, out conn): return false
  ├─ try: run "SELECT 1" on conn via SqlCommand.ExecuteScalarAsync(ct);
  │        return true
  └─ catch (any):
        sessions.TryRemove(ownerToken, out _);
        try { conn.Dispose(); } catch { /* connection already dead */ }
        return false

ReleaseAsync(key, ownerToken, ct)
  ├─ if !sessions.TryRemove(ownerToken, out conn): return  // already released
  ├─ try: await sp_releaseapplock(@Resource=Prefix+key,
  │                                @LockOwner='Session',
  │                                @DbPrincipal='public')
  └─ finally: await conn.DisposeAsync()
```

### Why this structure

- The lock lifetime IS the connection lifetime. Anything that loses the
  connection loses the lock. The registry only records which connection backs
  which `ownerToken`; SQL Server is still the source of truth for who holds
  what.
- `ownerToken` is already a fresh GUID per acquisition, so it makes a clean
  registry key. No additional bookkeeping needed in the core `DistributedLock`.
- Existing `OrionLockDiagnostics`, `ReentrancyRegistry`, and
  `DistributedLockHandle` (with its lease-renewal watchdog) work without change
  — the provider sits one layer below them and presents the same interface.

### Connection pooling

`Microsoft.Data.SqlClient` connection pooling defaults to enabled. Leaving it
on is correct: the connection only returns to the pool when *we* dispose it,
and at that point we have already called `sp_releaseapplock`. The pool's
`sp_reset_connection` on the next borrow is therefore harmless. The package
README will document this so users do not "fix" it by setting `Pooling=false`.

## 3. Public API surface

```csharp
namespace Moongazing.OrionLock.SqlServer;

public sealed class SqlServerLockOptions
{
    public string KeyPrefix { get; set; } = string.Empty;
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class SqlServerLockProvider : IDistributedLockProvider, IDisposable
{
    public SqlServerLockProvider(string connectionString, SqlServerLockOptions options);
    public Task<bool> TryAcquireAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken ct);
    public Task<bool> TryRenewAsync(string key, string ownerToken, TimeSpan leaseDuration, CancellationToken ct);
    public Task ReleaseAsync(string key, string ownerToken, CancellationToken ct);
    public void Dispose();
}

public static class OrionLockSqlServerBuilderExtensions
{
    public static OrionLockBuilder UseSqlServer(
        this OrionLockBuilder builder,
        string connectionString,
        Action<SqlServerLockOptions>? configure = null);
}
```

### Usage

```csharp
services.AddOrionLock()
    .UseSqlServer(
        "Server=...;Database=app;Trusted_Connection=true;",
        o => o.KeyPrefix = "myapp:");
```

### Design notes

- `KeyPrefix` matches `RedisLockOptions.KeyPrefix` semantically — multi-tenant
  or shared-instance namespacing.
- `CommandTimeout` is the per-command `SqlCommand.CommandTimeout`. The
  `sp_getapplock` call uses `@LockTimeout=0` so it returns immediately; the
  command timeout is the upper bound for network hangs only.
- Single overload: `UseSqlServer(connectionString, configure?)`. A factory
  overload can be added without breakage in v0.3+.
- No public `AppLockReturnCode` enum or low-level escape hatches — return-code
  handling stays private to the provider. Smaller public surface, easier to
  evolve.

### Error semantics on the public boundary

- `connectionString` null/whitespace → `ArgumentException` thrown by the
  extension method.
- Key length violation → `ArgumentException` from the provider before any
  network call.
- `sp_getapplock` rc < -1 → `OrionLockBackendException` wrapping the
  `SqlException` (so consumers can distinguish "backend failure" from
  `LockAcquisitionTimeoutException`).
- Raw `SqlException` (network down, auth failure, etc.) bubbles up. We do
  *not* convert it into a `false` from `TryAcquireAsync`; "could not reach SQL
  Server" is a different failure mode from "lock is held by someone else".

## 4. SQL call mechanics

### Acquire

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandType = CommandType.StoredProcedure;
cmd.CommandText = "sp_getapplock";
cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;

cmd.Parameters.Add(new SqlParameter("@Resource",    SqlDbType.NVarChar, 255) { Value = options.KeyPrefix + key });
cmd.Parameters.Add(new SqlParameter("@LockMode",    SqlDbType.VarChar,  32)  { Value = "Exclusive" });
cmd.Parameters.Add(new SqlParameter("@LockOwner",   SqlDbType.VarChar,  32)  { Value = "Session" });
cmd.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int)           { Value = 0 });
cmd.Parameters.Add(new SqlParameter("@DbPrincipal", SqlDbType.NVarChar, 32)  { Value = "public" });

var rc = new SqlParameter {
    ParameterName = "@RC", SqlDbType = SqlDbType.Int, Direction = ParameterDirection.ReturnValue };
cmd.Parameters.Add(rc);

await cmd.ExecuteNonQueryAsync(ct);
int returnCode = (int)rc.Value!;
```

Return-code handling:

| `returnCode` | Meaning | Provider action |
|:---:|---|---|
| `0` | Lock granted immediately | Register session, return `true` |
| `1` | Lock granted after wait | (Should not occur with `@LockTimeout=0`) Register, return `true` |
| `-1` | Wait timeout — already held | Dispose conn, return `false` |
| `-2` | Cancelled | Dispose conn, throw `OperationCanceledException` |
| `-3` | Deadlock victim | Dispose conn, throw `OrionLockBackendException` |
| `-999` | Parameter validation | Dispose conn, throw `OrionLockBackendException` |

### Renew

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT 1";
cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;
await cmd.ExecuteScalarAsync(ct);
return true;
```

Any exception → registry remove, connection dispose, return `false`. We do
*not* narrow the catch — any failure on this socket means the session is no
longer trustworthy.

### Release

```csharp
using var cmd = conn.CreateCommand();
cmd.CommandType = CommandType.StoredProcedure;
cmd.CommandText = "sp_releaseapplock";
cmd.CommandTimeout = (int)options.CommandTimeout.TotalSeconds;

cmd.Parameters.Add(new SqlParameter("@Resource",    SqlDbType.NVarChar, 255) { Value = options.KeyPrefix + key });
cmd.Parameters.Add(new SqlParameter("@LockOwner",   SqlDbType.VarChar,  32)  { Value = "Session" });
cmd.Parameters.Add(new SqlParameter("@DbPrincipal", SqlDbType.NVarChar, 32)  { Value = "public" });

try { await cmd.ExecuteNonQueryAsync(ct); }
finally { await conn.DisposeAsync(); }
```

Connection disposal in `finally`: even if `sp_releaseapplock` fails, the
session must close so SQL Server tears the lock down at session end.

### `@DbPrincipal = "public"` rationale

Application locks are scoped to a database principal. `public` is the role
every user is a member of; it gives the broadest, most portable visibility.
Multi-tenant isolation is handled at the `KeyPrefix` layer, not at the
principal. Exposing `@DbPrincipal` as an option is unnecessary attack surface
and can be added in v0.3+ if a real workload asks for it.

### Concurrency notes

- Provider is a singleton. Each `SqlConnection` is associated with exactly one
  `ownerToken` (one handle), and a handle's methods are sequential, so
  concurrent use of the same connection does not arise from a single consumer.
- The watchdog renewal path and the consumer's `Dispose` path can interleave:
  if `Dispose` removes a token from the registry while the watchdog's
  `SELECT 1` is mid-flight, the connection may be disposed under the
  watchdog. The resulting `ObjectDisposedException` is caught by the renewal
  exception path, which returns `false`. The handle is already being disposed,
  so `LostToken` firing at that moment is a harmless extra signal.

## 5. Lifecycle, error & lost-lease semantics

### Happy path

```csharp
await using var handle = await myLock.AcquireAsync("invoice:42", TimeSpan.FromSeconds(30));
// critical section
```

1. `DistributedLock.TryAcquireAsync` — no in-process reentry → `ownerToken = Guid.NewGuid()`
2. `SqlServerLockProvider.TryAcquireAsync` — open connection, `sp_getapplock` returns 0, register session
3. `DistributedLockHandle` wraps the result; watchdog renews every `LeaseDuration / 3` (10s here) via `SELECT 1`
4. `await using` disposes → watchdog stops → `provider.ReleaseAsync` → `sp_releaseapplock` + connection dispose

### Failure modes

| Scenario | Behaviour |
|---|---|
| Another holder (`sp_getapplock` rc=-1) | `TryAcquireAsync` returns `false`. `DistributedLock.AcquireAsync` enters its retry loop, waits up to `WaitTimeout`, then throws `LockAcquisitionTimeoutException` (unchanged from existing behaviour). |
| Backend error during acquire (rc < -1) | `OrionLockBackendException` thrown. |
| `SqlException` during acquire (network, auth) | Connection disposed, exception bubbles to caller — distinct from "lock unavailable". |
| Network drop during renewal | `SELECT 1` throws → registry remove + dispose → `TryRenewAsync` returns `false` → `DistributedLockHandle` trips `LostToken` → consumer observes `IsCancellationRequested`. |
| `SqlException` during release | Swallowed after disposing the connection. The session is dead, the lock is gone — surfacing a release-time failure has no recovery action. |
| Consumer forgets to dispose | Connection leaks until process exit, then SQL Server reclaims the session. Same contract as Redis/EF Core backends — we do not add a finalizer. |

### Lost-lease story

With Redis or the EF Core lock table, *lost lease* is observed when
clock-based expiry passes and another caller wins. With this backend, *lost
lease* means the SQL session is gone — verified directly by the failing
`SELECT 1`. False positives from clock skew are impossible on this backend.
README and CHANGELOG call this out as the user-visible difference between the
two SQL-Server-capable backends.

## 6. Lock key constraints & test strategy

### Lock key validation

`sp_getapplock @Resource` is `nvarchar(255)`. We require
`length(KeyPrefix) + length(key) <= 240` (16-char safety margin) and throw
`ArgumentException` otherwise:

```csharp
private static void ValidateKey(string key, string prefix)
{
    if (string.IsNullOrWhiteSpace(key))
        throw new ArgumentException("Lock key cannot be null or whitespace.", nameof(key));

    var total = prefix.Length + key.Length;
    if (total > 240)
        throw new ArgumentException(
            $"Lock key (with prefix) is {total} characters; SQL Server sp_getapplock @Resource is " +
            "limited to ~240 characters. Hash long keys on the caller side or shorten the prefix.",
            nameof(key));
}
```

**Collation note (will be in the package README):** `sp_getapplock @Resource`
is compared using the server's default collation, which on stock installs is
case-insensitive. `"Invoice:42"` and `"invoice:42"` will collide. This differs
from the Redis (case-sensitive) and EF Core (column-collation-dependent)
backends. Consumers must be aware.

**No automatic hashing.** Silent transformation of keys creates surprising
collisions. An early loud error is safer.

### Test strategy

`tests/Moongazing.OrionLock.SqlServer.Tests/` mirrors the
`RedisLockProviderTests` shape, using `Testcontainers.MsSql`:

```csharp
public sealed class SqlServerLockProviderTests : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder().Build();
    private string connectionString = default!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        connectionString = container.GetConnectionString();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    private SqlServerLockProvider NewProvider()
        => new(connectionString, new SqlServerLockOptions());
}
```

#### Test matrix

| Test | Asserts |
|---|---|
| `TryAcquire_ShouldSucceedThenBlockSecondOwner` | Baseline mutual exclusion. |
| `TryAcquire_ShouldHandOutExactlyOne_AcrossParallelCallers` | 5 parallel acquires → exactly 1 success. |
| `TryRenew_ShouldExtendForOwner_AndRejectNonOwner` | Owner-known renews succeed; unknown owner returns `false`. |
| `Release_ShouldOnlyReleaseForOwner` | Unknown owner is a no-op; correct owner releases. |
| `TryRenew_ShouldReturnFalse_AfterConnectionDrop` | **SqlServer-specific.** Acquire, then `KILL @@SPID` from a side connection. Next renewal returns `false`. |
| `Release_ShouldBeNoOp_ForUnknownOwnerToken` | No exception when releasing a token never seen. |
| `KeyLengthLimit_ShouldThrowEarly` | Key >240 chars throws `ArgumentException`; no DB call made. |
| `Dispose_ShouldReleaseAllOpenSessions` | Acquire 3, dispose provider, verify all locks released (next acquire succeeds for each). |
| `SmokeTest` (end-to-end) | `AddOrionLock().UseSqlServer(...)` → `AcquireAsync` → critical section → dispose; two consumers serialize correctly. |

Concurrency stress (multi-process) is **not** in this spec — it is v0.2.0's
fourth work item.

#### CI note

`Testcontainers.MsSql` requires Docker (GitHub Actions Linux runners have it,
the existing `Testcontainers.Redis` job proves this). MsSql is heavier than
Redis (~1.5 GB image, ~30 s cold start). CI duration grows by ~30-60 s — the
alternative (LocalDB) is Windows-only and not viable.

## 7. Documentation, branch & release plan

### Documentation updates (in this PR)

- `src/Moongazing.OrionLock.SqlServer/docs/PackageReadme.md` — new NuGet
  package README, same template as Redis and EF Core packages. Covers quick
  start, `sp_getapplock` characteristics, case-insensitivity warning, 240-char
  key limit, pooling note.
- Root `README.md` — one row added to the backend matrix:
  `SqlServer | sp_getapplock | session-scope | crash-safe`.
- `CHANGELOG.md` — `[Unreleased]` "Added": `OrionLock.SqlServer backend using
  sp_getapplock with session-scope lifetime.`
- `docs/lease-and-renewal.md` — short note that the SqlServer backend's "lease"
  is connection-scoped, not clock-based.
- `docs/migrations/` — no entry (no schema involved).

### Not in this PR

- `sample/` updates — done in a follow-up commit after all four v0.2.0 parts
  land, so the sample demonstrates the full v0.2.0 surface at once.
- `bench/` updates — same reasoning; benchmark refresh happens just before the
  v0.2.0 release commit.
- `ROADMAP.md` does not change in this PR. v0.2.0 stays in *Planned* until
  release.

### Solution

Add the new src and test projects to `Moongazing.OrionLock.sln` via
`dotnet sln add`.

### Branch & PR

- Branch: `feat/sqlserver-backend` from `main`.
- Commits follow the existing style (`feat(orionlock): ...`, small and
  focused).
- PR title: `feat(orionlock): SqlServer backend using sp_getapplock`.
- Merge to `main` is the gate, not a release.

### v0.2.0 release sequence (after all four work items merged)

1. `chore(release): v0.2.0` commit:
   - `Directory.Build.props`: `<Version>0.2.0</Version>`
   - `CHANGELOG.md`: dated `[0.2.0]` section with all four work items
   - `ROADMAP.md`: move v0.2.0 entry to *Released*
2. `git tag v0.2.0` and push the tag.
3. Existing tag-push workflow publishes all four packages to NuGet.

## Open questions

None at spec approval. Any new questions surfaced during implementation are
captured as inline TODOs in the implementation plan, not the spec.
