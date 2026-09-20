<!-- markdownlint-disable MD024 -->
# Changelog

All notable changes to OrionLock are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Fencing tokens: `IDistributedLockHandle.FencingToken`.** A distributed lock cannot stop a holder from
  being wrong about still holding it. Your process pauses — a stop-the-world GC, a VM migration, a
  throttled container — for longer than its lease. None of your code runs, so nothing notices: `IsHeld`
  is never false and `LostToken` never trips, because renewal never ran to fail. The backend expires the
  key, another process acquires it legitimately, and then yours resumes mid-critical-section and writes.
  Every lease-based lock has this hole; the only known fix is a fencing token.

  `handle.FencingToken` is a `long?` that strictly increases with each acquisition **of that key**, across
  processes. Pass it to the resource you are protecting and have the resource refuse any write carrying a
  token **lower** than the highest it has already accepted — then the resumed process is turned away by
  the only participant in a position to know. A token equal to the mark is the current holder writing
  again and must be accepted: the token identifies the acquisition, not the write, and stays stable for
  the whole hold. The check must also be one statement with the write, not a `SELECT` then an `UPDATE`;
  [README](README.md#fencing-tokens) and [docs/fencing-tokens.md](docs/fencing-tokens.md) have the SQL
  (`last_fence <= @fence`) and the reasoning.

  **Which backends provide one.** `null` means the backend has nothing it can make strictly monotonic, and
  OrionLock reports that rather than inventing a counter you would then trust:

  | Backend | Token | Default |
  | --- | --- | --- |
  | etcd | mvcc revision of the acquiring transaction | on, no extra round trip |
  | Redis | `INCR` on a per-key counter, in the same Lua call as the `SET NX PX` | off — `RedisLockOptions.FencingTokens` |
  | EF Core | `FencingToken` column, bumped by the same `UPDATE` that takes the row | on, after a migration |
  | Consul | KV `ModifyIndex` (the Raft log index) | off — `ConsulLockOptions.FencingTokens` |
  | Testing (in-memory) | per-key counter | on |
  | ZooKeeper, PostgreSQL, SQL Server | — | `null` |

  Redis is opt-in because the counter key can never expire or be deleted — one that restarted would
  reissue a token an earlier holder already spent — so enabling it leaves one small permanent key per lock
  key. Those counters live under a reserved `orionlock-fence:` prefix, and with fencing on, a lock key
  that would resolve into that namespace is rejected with an `ArgumentException` rather than becoming both
  a lock and another key's counter; with the default `orionlock:` key prefix that can never happen.
  Consul is opt-in because its acquire returns no index, so the token costs one extra GET — and a read
  that comes back empty fails the acquire (releasing the key) rather than returning an untokened hold over
  a lock that may already be gone.
  ZooKeeper reports `null` because both numbers it offers (the sequential znode's suffix and the parent's
  `cversion`) reset when the provider prunes the empty parent znode; PostgreSQL advisory locks and SQL
  Server `sp_getapplock` report `null` because they keep no per-key state to count, and the schema-free
  alternatives are only non-decreasing, not strictly increasing. Use the EF Core backend on those two when
  you need fencing.

  **What to do.** Nothing, if you do not want a token: existing callers, existing observers and existing
  custom `IDistributedLockProvider` implementations all compile and behave identically. If you do want one,
  call `handle.RequireFencingToken()` rather than reading the property — it throws when the backend mints
  none, instead of letting a `null` reach your comparison and silently disable the protection. The token is
  also on `ILockEventObserver.OnAcquired(key, durationMs, fencingToken)` and on the acquire span as
  `orionlock.fencing_token` (never a metric tag — it is unique per acquisition). `FencingGuard` does the
  high-water-mark bookkeeping for a resource that genuinely lives in this process.

- **`OrionLock.EntityFrameworkCore`: a `FencingToken` column on `OrionLock_Locks`.** Requires a migration;
  see [docs/migrations/orionlock-locks-table.md](docs/migrations/orionlock-locks-table.md) for the
  `dotnet ef` command and the per-provider DDL. If you cannot add the column yet, `Ignore()` the property
  in your model: the provider reads column names from the EF model, finds nothing mapped, and emits exactly
  the SQL it emitted before fencing existed while reporting no token. Locking behaviour is unchanged either
  way.
### Changed

- **BREAKING: a lock key is now one opaque name, validated in the core — `/` is no longer legal in a key.**
  The key is caller data, and two backends spliced it into a namespace they do not own: the Consul
  provider built an HTTP path out of it and ZooKeeper built a znode path. Both were hardened in place,
  which left seven backends with three different ideas of what a key is — `/` meant "namespace hierarchy"
  on Consul and ZooKeeper and nothing on the other five, so the same key addressed different things
  depending only on which backend you registered. **Used to happen:** a key like `tenant-1/orders` was a
  two-level KV path on Consul, one flattened `tenant-1~002Forders` znode on ZooKeeper, and a literal
  string with a slash in it on Redis, SQL Server, PostgreSQL, EF Core and the in-memory backend; a key
  like `../session/destroy/abc` was refused by Consul and ZooKeeper only, and a key with a control
  character or 5 000 characters in it was refused by SQL Server only, at different points in the call.
  **Happens now:** `LockKey.Validate` runs in the core before any backend sees the key and throws
  `ArgumentException` on the caller's own thread at acquire time — naming the offending key — for a key
  containing `/`, for the relative names `.` and `..`, for control characters (`U+0000`–`U+001F`,
  `U+007F`–`U+009F`) and the ranges ZooKeeper refuses in a znode name (`U+D800`–`U+F8FF`,
  `U+FFF0`–`U+FFFF`), and for a key longer than `LockKey.MaxLength` (200 — the bound the EF Core row
  already mapped `Key` at, and one that fits SQL Server's ~240-character `@Resource` budget with a
  prefix). The core validates and rejects only; it does not percent-encode, because a URI path, a znode
  name and a Redis key are different alphabets — each backend still encodes what survives.
  **What to do:** move hierarchy out of the key and into the backend's own namespace knob, which every
  backend already has: `RedisLockOptions.KeyPrefix`, `ConsulLockOptions.KeyPrefix`,
  `SqlServerLockOptions.KeyPrefix`, `PostgresLockOptions.KeyPrefix`, `ZooKeeperLockOptions.RootPath`,
  `EtcdLockOptions.KeyPrefix`. `lock.AcquireAsync("tenant-1/orders")` becomes
  `UseConsul(..., o => o.KeyPrefix = "orionlock/tenant-1/")` plus `AcquireAsync("orders")`, or simply
  `AcquireAsync("tenant-1:orders")` if the separator carried no meaning. Keys already held on a backend
  keep their existing on-the-wire names: this changes what you may ask for, not how a valid key is
  encoded. `ConsulKvPath` is now only a percent-encoder and no longer rejects anything itself.

- **BREAKING: every backend registers the same way, and registering two backends now throws.**
  The library carried two opposite DI conventions behind one identical composition-root shape. Redis,
  SQL Server, PostgreSQL, EF Core and Testing registered with `TryAddSingleton` (first registration
  won); Consul, etcd and ZooKeeper used `RemoveAll` + `AddSingleton` (last registration won).
  **Used to happen:** `AddOrionLock().UseInMemory().UseRedis("...")` ran the **in-memory fake in
  production**, silently, because Redis could not overwrite the fake's earlier registration — while the
  same code with `UseConsul(...)` in place of `UseRedis(...)` ran Consul. Which of the two you got was
  decided entirely by which backend you picked. **Happens now:** all nine `Use*` registrations go
  through the new `OrionLockBuilder.UseBackend(backendName, factory)`, and asking for a second, different
  backend on the same builder throws `InvalidOperationException` naming both. Exactly one backend backs
  one `IDistributedLock`, so two of them is a composition-root mistake rather than a preference to
  resolve silently. **What to do:** keep the one `Use*` call you meant and delete the other. Registering
  the *same* backend twice still works and replaces the earlier registration, so a later `UseRedis(...)`
  with different options wins as you would expect; a test host that deliberately overrides the
  production registration starts a fresh builder with another `AddOrionLock()` call, which replaces the
  provider without the guard. Custom backends should call `UseBackend` rather than registering
  `IDistributedLockProvider` by hand.

### Fixed

- **BREAKING (behaviour): reentrancy is now scoped to the flow that holds the lock, not to the key.**
  Same-process reentrancy was keyed on the lock key alone, and `AddOrionLock` registers
  `IDistributedLock` as a singleton — so two *unrelated* callers on the same instance (two concurrent
  HTTP requests, say) asking for the same key both got a handle: the second was handed a nested handle
  over the first one's lease, the backend was never consulted, and both ran inside the critical section
  at once. This defeated mutual exclusion for every in-process caller of a shared `IDistributedLock`.
  An acquire now establishes an owner identity in the calling flow (an `AsyncLocal` scope, like
  `Activity.Current`); only a re-acquire from that same flow — including one made deeper in the call
  stack, after any number of `await`s — collapses into a nested handle. Any other caller goes to the
  backend and contends normally.

  **What to check:** if you have in-process callers that were *silently* sharing a lease, they will now
  block against each other and `AcquireAsync` can throw `LockAcquisitionTimeoutException` (or
  `TryAcquireAsync` return `null`) where it previously returned immediately. That is the correct
  behaviour and almost certainly what you wanted, but it can surface as new contention or new timeouts
  under load. Deliberate reentrancy is unaffected as long as the nested acquire runs inside the flow
  that took the outer one. The scope belongs to the hold, not to the flow: work forked off *during* the
  critical section inherits it and still re-enters, while work forked after the handle was released does
  not, and neither does work started from an independent context.

- **A nested handle is no longer handed out over a lease that has already been lost.** If the renewal
  watchdog surrendered the lease (renewal failure past `RenewalFailureGracePeriod`, or a backend-
  confirmed loss), a re-acquire from the same flow still got a nested handle over that dead lease —
  non-null, with nothing holding the key at the backend. The registry now checks the real handle is
  still held, and falls through to a genuine backend acquire when it is not. If you were relying on a
  nested acquire always succeeding, check `IsHeld` / `LostToken` on the outer handle: it was already
  telling you the lease was gone.

- **A lock handle no longer stops renewing its lease in silence when the backend raises an unrelated
  cancellation.** The exclusive handle's renewal watchdog caught *every* `OperationCanceledException`
  from `TryRenewAsync` and returned. If a provider surfaced a cancellation that was not the handle's own
  dispose (a client-library timeout token, an ambient request token threaded into the backend call), the
  watchdog stopped renewing while `IsHeld` stayed `true` and `LostToken` never tripped — so the lease
  quietly expired at the backend while your code went on believing it held the lock. Only the handle's
  own dispose is terminal now; any other cancellation is treated as a transient renewal failure and the
  watchdog keeps retrying, surrendering through the normal `RenewalFailureGracePeriod` path if the
  backend stays unreachable. The reader-writer handle already behaved this way. If you had code watching
  for the watchdog to go quiet, watch `LostToken` instead — it now actually fires.

- **`IsHeld` and `LostToken` now tell the truth when `AutoRenew = false`.** With auto-renew off no
  watchdog runs, and nothing ever observed the lease running out: `IsHeld` stayed `true` and `LostToken`
  never tripped, however long after a TTL backend had expired the key and possibly handed it to someone
  else. A handle taken with `AutoRenew = false` against a TTL backend now trips `LostToken` and reports
  `IsHeld = false` once `LeaseDuration` has elapsed. The expiry runs the same surrender the renewal
  watchdog runs on a confirmed loss, so `orion.lock.lease.lost` and
  `orion.lock.lease.expired_before_release` are counted, `orion.lock.leases.held_concurrent` comes back
  down, and a registered `ILockEventObserver` sees `OnLeaseLost` (and no longer a misleading
  `OnReleased` when the handle is disposed afterwards). Session-scoped backends (PostgreSQL advisory locks,
  SQL Server `sp_getapplock`), where the hold legitimately outlives `LeaseDuration`, are unaffected. If
  you used `AutoRenew = false` with a short lease for long work and read `IsHeld`, it will now go false
  at the lease deadline — that was always the real state; raise `LeaseDuration` or turn auto-renew on.

- **`LostToken` no longer throws `ObjectDisposedException` after the handle is disposed.** It was read
  straight off the `CancellationTokenSource`, which throws once disposed, so a `finally` or logging path
  that touched the handle after `await using` blew up. The token is captured at construction and stays
  readable for the handle's whole life. Applies to both the exclusive and the reader-writer handle.

- **The renewal watchdog no longer surrenders a session-scoped hold when renewals keep failing.** After
  `RenewalFailureGracePeriod` of failing renewals the watchdog gives the lease up, on the reasoning that
  the backend's TTL has expired by now anyway. That is false for a backend whose hold is scoped to an
  open session rather than to a wall-clock TTL: there the lock is provably still ours, and surrendering
  let a second holder into the critical section. The surrender is now gated on the backend declaring
  `LeaseDurationIsTtl`; on session-scoped backends the watchdog keeps retrying instead. Both the
  exclusive and the reader-writer handle.

- **The blocking `AcquireAsync` now polls under one owner token instead of a new one per retry.** Every
  retry minted a fresh owner token, so a contended acquire looked to the backend like a stream of
  different acquirers — which breaks fencing identity and can orphan state a partly-succeeded attempt
  left behind under a token no later retry can reclaim. (The code's own comments already claimed it
  reused one token, and `SharedExclusiveLock.AcquireAsync` genuinely did.) If you log or fence on the
  owner token, a contended acquire now shows one token for the whole wait rather than one per attempt.

- **A blocking `AcquireAsync` no longer waits past `WaitTimeout` when `RetryInterval` is longer than the
  remaining budget.** The full `RetryInterval` was slept before the deadline was re-checked, so with,
  say, `WaitTimeout = 200ms` and `RetryInterval = 10s`, `LockAcquisitionTimeoutException` arrived after
  about 10 seconds. The poll delay is now clamped to the time left, matching the reader-writer lock and
  the deadline overloads. If you had padded timeouts to absorb the overshoot, you can drop the padding.

- **`InProcessFifoWaiterCoordinator` reports the caller's cancellation token and stops leaking
  registrations.** A cancelled FIFO waiter raised an `OperationCanceledException` carrying
  `CancellationToken.None` instead of the token the caller passed, so callers could not tell their own
  cancellation apart from anyone else's. The same waiter also never disposed its
  `CancellationTokenRegistration` — it is only disposed in `LeaveAsync`, which a caller that never
  received its ticket can never reach — so each cancelled wait stayed rooted in the caller's
  `CancellationTokenSource` until that source was disposed. Both are fixed; a cancellation-heavy
  workload sharing one long-lived token no longer accumulates dead registrations.

- **The internal measuring decorator no longer reports every backend as a TTL backend.**
  `AddOrionLock` wraps the registered `IDistributedLockProvider` in an internal measuring decorator, and
  that decorator did not forward `LeaseDurationIsTtl` — it fell back to the interface default of `true`.
  A session-scoped backend that overrides the flag to `false` (PostgreSQL advisory locks, SQL Server
  `sp_getapplock`) therefore had its override discarded the moment it went through DI, so anything gated
  on the flag behaved as if the lease were a wall-clock TTL. The decorator now forwards the inner
  provider's value. If you relied on the `orion.lock.lease.expired_before_release` counter firing for a
  session-scoped backend, it will now correctly stay silent there.
- **EF Core: two hosts with clock drift could hold the same exclusive lock.** `EfCoreLockProvider`
  computed lease deadlines from `DateTime.UtcNow` on the application host, then compared them against a
  deadline some other host had written with *its* clock. Hosts whose clocks differed by more than the
  lease both saw the row as expired and both took it. Every instant now comes from the database server —
  the same single authoritative clock the reader-writer providers already use — so drift between your
  application hosts no longer affects mutual exclusion. Note that SQLite's `CURRENT_TIMESTAMP` has
  whole-second resolution, so leases under about two seconds are not meaningful on SQLite.
- **EF Core: the "provider-agnostic" SQL only ran on SQLite.** The table name and the `Key` column were
  spliced into raw SQL unquoted. `Key` is a reserved word in T-SQL and MySQL, where the statement is a
  syntax error, and PostgreSQL case-folded the bare table name to `orionlock_locks`, which does not
  exist. Identifiers now come from your EF model and are quoted by the active provider, so renamed
  tables, schemas and remapped columns work too. The provider is now exercised against real PostgreSQL
  and SQL Server in CI, not SQLite alone.
- **EF Core: a lost race on a brand-new key threw instead of returning `false`.** The first-use
  `INSERT ... WHERE NOT EXISTS` is not atomic under READ COMMITTED, and its `catch` named
  `DbUpdateException`, which raw SQL never raises — so a genuine primary-key violation escaped
  `TryAcquireAsync` as a raw driver exception. A genuine unique / primary-key violation is now caught and
  reported as "you did not get the lock". Only that one error is: a not-null, foreign-key or check
  violation — which a customised lock-table mapping, an added constraint or a trigger can raise — still
  reaches you as an exception rather than being retried forever as contention against a schema that can
  never accept the row.
- **etcd: a successful lease renewal could be reported as a lost lease.** The keep-alive resolved its
  result in a race with its own response callback, so the handle could trip its lost-token and revoke
  the lease while your code was still inside the critical section. Only etcd itself now decides: `false`
  means the server said the lease is gone. A keep-alive stream that ends without any answer is treated
  as a transient backend fault and throws, which the renewal loop already retries under its grace period
  instead of surrendering the lock.
- **Consul: a key freed by session invalidation was available before its old holder knew.** See the
  breaking note below.
- **Consul: a lock key could retarget the request at a different Consul endpoint.** Keys are spliced into
  the `/v1/kv/{key}` HTTP path, where URI canonicalisation collapses `/../` and `?`/`#` splice or
  truncate the request. Segments are now percent-encoded, and a key with an empty or relative (`.`,
  `..`) segment is rejected with `ArgumentException`.
- **ZooKeeper: the provider now declares itself session-scoped.** A ZooKeeper hold lasts as long as its
  session, not for a TTL, but the provider left `LeaseDurationIsTtl` at the default `true`. That produced
  false `expired_before_release` events for callers legitimately still holding the lock, and reported a
  lost lease after a connection blip that merely delayed a renewal.
- **ZooKeeper: a lock key containing `/` grew the ensemble's tree with no way back.** Each `/`-separated
  segment became a persistent znode that release never deleted, and ZooKeeper keeps its whole tree in
  memory. A key is now encoded into exactly one znode name, and a key's parent znode is deleted once its
  last holder releases. Express hierarchy through `ZooKeeperLockOptions.RootPath`, not through the key.
- **Redis: a sub-millisecond lease was destructive rather than short.** `RedisLockProvider` floored the
  lease to whole milliseconds, so a positive sub-millisecond lease became `PEXPIRE key 0` — which
  *deletes* the key, meaning the first renewal dropped the lock. Leases now round up to at least 1 ms,
  and a zero or negative lease is rejected with `ArgumentOutOfRangeException` instead of silently
  producing a broken lock. The provider also now validates `key` and `ownerToken` and honours the
  `CancellationToken` on acquire and renew (release deliberately still runs, so a cancelled caller cannot
  strand the lock).
- **Redis: FIFO waiters were not actually ordered within a millisecond.** A sorted-set score is a double,
  and the coordinator's packing overflowed its exact-integer range, so runs of about 16 same-millisecond
  waiters collapsed onto one score and fell back to arbitrary ordering. The packing now stays inside that
  range, so arrival order holds. **The score encoding changed:** during a rolling upgrade, waiters queued
  on the same key by old and new coordinators order against each other incorrectly for one window.
- **Testing: `InMemorySharedExclusiveLockProvider` could hand out an already-expired hold.** It read the
  clock before taking the per-key lock, so a thread that waited stamped expiries against a stale instant.
  The clock is now read under the lock.

- **A reader-writer hold now emits the signals the exclusive one always did.** The README promised the
  reader-writer surface's lease, renewal, release and diagnostics semantics mirror the exclusive
  `IDistributedLock`. They did not. A `Shared` or `Exclusive` hold taken through `ISharedExclusiveLock`
  never emitted `orion.lock.lease.renewal_failures_consecutive`,
  `orion.lock.lease.grace_period_exhausted`, `orion.lock.handle.renewals_per_hold` or
  `orion.lock.acquire.attempt_count`, and — worst of the set — never invoked a registered
  `ILockEventObserver` at all: `OnAcquired`, `OnAcquireTimedOut`, `OnLeaseLost` and `OnReleased` were
  all silent for every reader-writer hold, so a consumer registering an observer for an audit trail got
  a complete blank with nothing in the API to suggest it. Both handles now drive one internal
  `LeaseWatchdog`, so the two paths emit the same instruments in the same order by construction rather
  than by intention, and a new `SharedExclusiveLock(provider, eventObserver)` overload threads the
  observer through. Nothing on the exclusive path changed, and no signal changed its ordering or
  timing. If you alert on these instruments, expect reader-writer traffic to start appearing in them.
  **Note:** the shipped backend packages still construct `SharedExclusiveLock` without an observer, so
  `ISharedExclusiveLock` resolved from `UseRedis` / `UsePostgres` / `UseEntityFrameworkCore` /
  `UseInMemory` now passes the DI-registered observer through as well, so a reader-writer hold reports
  to it exactly as an exclusive one does.

### Changed

- **CI runs with least privilege and pinned actions.** The workflow declares
  `permissions: contents: read` (the publish job keeps its own `packages: write`), every checkout
  sets `persist-credentials: false` so the token is not written into `.git/config` before the job
  builds and runs branch code, and `actions/checkout` / `actions/setup-dotnet` are pinned to commit
  SHAs. The NuGet and GitHub Packages tokens move off the command line into `env:`, the release
  build and pack run with `ContinuousIntegrationBuild=true`, and the GitHub Packages push no longer
  hides failures behind `continue-on-error`: it tolerates an already-published version and fails on
  anything else. The pre-pull step now names the SQL Server image the tests actually use
  (`2022-latest`), which it stopped doing when the suites moved to Testcontainers 4.
- **BREAKING (Consul, default value): `ConsulLockOptions.LockDelay` now defaults to 5 seconds instead of
  `TimeSpan.Zero`.** The zero default removed the mechanism that makes a Consul session lock safe under
  partition: Consul invalidating a session does not stop the process that held it, so with no delay a new
  holder can enter the critical section while the old one is still inside it. The delay is *not* paid on
  a normal release — `ReleaseAsync` releases the KV entry before destroying the session — only on the
  crash and partition paths. If you raise `RenewalFailureGracePeriod` above the Consul session TTL you
  must raise `LockDelay` by the same amount; the package README states the rule. Set it back to
  `TimeSpan.Zero` only if you accept that a partitioned holder and its successor can overlap.

- **The container-backed tests skip instead of failing when Docker is absent.** The Redis, PostgreSQL,
  SQL Server and EF Core suites hard-failed on a machine with no Docker daemon - 135 red tests that said
  nothing about the code and hid the ones that did. They now carry `[DockerFact]` / `[DockerTheory]`,
  which probe the Docker endpoint once per run and skip with a reason. Test only; no shipped code changed.
- **Testcontainers 3.10.0 -> 4.15.0 across the test suites.** 3.10.0 pulled `SSH.NET 2023.0.0`
  transitively, which carries [GHSA-q939-rpr3-3284](https://github.com/advisories/GHSA-q939-rpr3-3284)
  (High) and raised NU1903 on every restore. Nothing shipped ever referenced it. Testcontainers 4 also
  requires an explicit image, so the Redis, PostgreSQL and SQL Server tags are now pinned in one place
  instead of taken from the library defaults.

## [2.0.0] - 2026-07-29

### Changed

- **BREAKING (telemetry only): adopted the family `orion.lock.*` metric-naming convention.** Every
  OpenTelemetry instrument was renamed from the `orionlock.*` prefix to `orion.lock.*`, matching the
  `orion.<component>.<instrument>` convention the rest of the Orion family now uses. This is why the
  release is a major version bump even though **no code API changed** — the meter / activity-source
  name (`Moongazing.OrionLock`), every tag key (`backend`, `result`) and value, the span names, the
  static-tags mechanism (`WithMetricsLabel`), and all public types are unchanged.

  Rename every dashboard, alert, and recording rule. The full instrument list moved as follows (each
  `orionlock.<name>` became `orion.lock.<name>`):

  - Counters: `orion.lock.acquisitions`, `orion.lock.contentions`, `orion.lock.lease.lost`,
    `orion.lock.acquire.timeout`, `orion.lock.acquire.cancelled`, `orion.lock.lease_renewal.failures`,
    `orion.lock.lease.expired_before_release`, `orion.lock.lease.grace_period_exhausted`,
    `orion.lock.health_check.result`.
  - Histograms: `orion.lock.acquire.duration`, `orion.lock.acquire.latency`,
    `orion.lock.acquire.attempt_count`, `orion.lock.contention.duration`,
    `orion.lock.lease_renewal.duration`, `orion.lock.handle.holding_duration`,
    `orion.lock.handle.renewals_per_hold`, `orion.lock.reentrancy.max_depth`,
    `orion.lock.fairness.queue_depth`, `orion.lock.fairness.coordinator_enter_duration`,
    `orion.lock.lease.renewal_failures_consecutive`.
  - UpDownCounters: `orion.lock.reentrancy.depth`, `orion.lock.leases.held_concurrent`.
  - Observable gauge: `orion.lock.health.last_check_at_unix_seconds`.

  OrionLock follows the naming convention by string (it does not reference `Orion.Abstractions`), so
  its dependency footprint is unchanged.

## [1.0.1] - 2026-07-27

### Fixed

- The package icon is now packed for every packable project from `Directory.Build.props`, so
  sub-packages added later inherit it automatically. `OrionLock.Consul`, `OrionLock.Etcd`, and
  `OrionLock.ZooKeeper` previously shipped with no icon because `PackageIcon` and the icon pack
  item were declared per-csproj on only some projects. Per-project READMEs are unchanged; this is
  a packaging-only fix with no runtime behavior or public API change.

### Security

- Pinned `SQLitePCLRaw.bundle_e_sqlite3` to `2.1.12` in
  `Moongazing.OrionLock.EntityFrameworkCore.Tests` to resolve
  [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) (High), a vulnerability
  in the bundled SQLite native library. `Microsoft.EntityFrameworkCore.Sqlite` resolved
  `SQLitePCLRaw.lib.e_sqlite3` `2.1.10` transitively; pinning the bundle lifts `core`,
  `lib.e_sqlite3`, and `provider.e_sqlite3` to the patched `2.1.12` together.
- This advisory reaches **test projects only**. SQLite is used solely as an in-test database for
  the EF Core provider's test suite; no shipped OrionLock package references SQLitePCLRaw, either
  directly or transitively. No released version of any OrionLock package is affected, and this
  change alters no runtime behavior and no public API.

## [1.0.0] - 2026-06-30

The 1.0 release is a stabilization and finalization milestone, not a feature release. It changes
no runtime behavior and breaks no existing public API: it commits to the current public surface
and hardens it. Everything that was callable in 0.6.0 is callable in 1.0.0 with identical
semantics; from here, public-surface changes inside the 1.x line are additions only.

### Public API freeze

- Added `Microsoft.CodeAnalysis.PublicApiAnalyzers` to each of the six packable projects
  (`OrionLock`, `OrionLock.Redis`, `OrionLock.EntityFrameworkCore`, `OrionLock.SqlServer`,
  `OrionLock.Postgres`, `OrionLock.Testing`), with a `PublicAPI.Shipped.txt` baseline that
  captures the current public surface of each and an empty `PublicAPI.Unshipped.txt`, both wired
  as `AdditionalFiles`. The surface was captured as-is, with no changes made to make it pass.
- This makes the surface a build-enforced contract: with `TreatWarningsAsErrors` on, any future
  public-surface addition, change, or removal fails the build (RS0016 / RS0017) until the
  `PublicAPI.*.txt` files are edited deliberately. The `.txt` baselines are the API-stability
  guard for the 1.x line.
- The frozen contract spans `IDistributedLock`, `IDistributedLockHandle`, `DistributedLockOptions`,
  the provider primitive interfaces (`IDistributedLockProvider`, `ISharedExclusiveLockProvider`),
  `ISharedExclusiveLock` / `LockMode`, the FIFO coordination contracts, the diagnostics surface,
  and the bundled backends' DI helpers.

### Trimming and Native AOT audit

- `OrionLock` (core) and `OrionLock.Testing` are now marked `IsTrimmable` and `IsAotCompatible`
  and build clean with the trim and AOT analyzers enabled under `TreatWarningsAsErrors`. The core
  uses no dynamic code generation; its only reflection reads assembly and attribute metadata for
  telemetry (`MeterVersion`, `BackendNameResolver`), which is trimmer- and AOT-safe and needs no
  suppression.
- The database and Redis backends (`OrionLock.Redis`, `OrionLock.EntityFrameworkCore`,
  `OrionLock.SqlServer`, `OrionLock.Postgres`) are documented as not claimed AOT-safe, because they
  depend on drivers (`StackExchange.Redis`, EF Core's dynamic model building,
  `Microsoft.Data.SqlClient`, `Npgsql`) whose own trimming posture OrionLock cannot assert. The
  per-package AOT posture is documented in the README.

### Documentation

- README pass with runnable, copy-pasteable examples for the main scenarios: acquire/release of an
  exclusive lock, a reader-writer lock with readers and a writer, `TryAcquireAsync` with a deadline
  (exclusive and reader-writer), and choosing a backend (in-memory, Redis, PostgreSQL). The
  distributed reader-writer matrix (Redis, PostgreSQL, EF Core) and the per-package trimming/AOT
  posture are documented. Stale version references were refreshed to the frozen 1.x surface.

### Notes

No behavioral, locking, lease, renewal, fairness, or wire-format changes ship in 1.0.0. The full
existing test suite passes unchanged; the `PublicAPI.Shipped.txt` baselines are the new
build-time guard against accidental surface drift.

## [0.6.0] - 2026-06-27

### Added

#### Provider-portable EF Core distributed reader-writer (shared/exclusive) lock

The third distributed `ISharedExclusiveLockProvider`, and the first that is provider-portable: `EfCoreSharedExclusiveLockProvider` ships in the existing `OrionLock.EntityFrameworkCore` package and brings the v0.4.0 reader-writer seam to ANY relational EF Core provider (SQL Server, PostgreSQL, and others) through provider-agnostic EF Core, not raw provider SQL. For a given key, any number of `Shared` (read) holders coexist, OR exactly one `Exclusive` (write) holder owns it, with the same guarantees the Redis and PostgreSQL providers give: mutual exclusion, per-reader TTL reclaim, renewal, fencing, and lease-bounded writer fairness. The exclusive-only EF Core lock-table backend (`OrionLock_Locks`) and its wire format are unchanged; this is purely additive.

- `EfCoreSharedExclusiveLockProvider` implementing `ISharedExclusiveLockProvider`. Holds are clock-leased rows in `OrionLock_RwHolds` (`OrionLockRwHoldRow`): a `Kind='r'` row per reader keyed by `(Resource, OwnerToken)`, one `Kind='w'` writer row, one `Kind='pw'` pending-writer row, each carrying an explicit `ExpiresOnUtc`. This mirrors the PostgreSQL `orionlock_rw_holds` schema exactly, but driven through EF Core so it is not PostgreSQL-specific. Consequently `LeaseDurationIsTtl` is `true` (rows reclaimed by time), like the Redis and PostgreSQL reader-writer providers and unlike the session-scoped exclusive backends.
- Readers are tracked individually (never a bare counter), so one reader's expiry never frees another's. Expired rows are pruned by `ExpiresOnUtc <= now` at the start of every transition.
- **Per-resource serialization without provider-specific lock hints.** A relational EF Core provider exposes no portable advisory lock or `FOR UPDATE`, so every transition runs in a `Serializable` transaction and first writes the resource's anchor row in `OrionLock_RwResources` (`OrionLockRwResourceRow`) with an unconditional `UPDATE`. Two concurrent transitions for the SAME resource therefore both write that one row under serializable isolation and the database's own concurrency control forces one to fail with a serialization / deadlock conflict; the loser is retried (bounded, short linear backoff) and re-reads fresh state. This makes the prune-evaluate-write sequence race-free per resource on any relational provider, the portable analogue of the PostgreSQL `pg_advisory_xact_lock`. Distinct resources write distinct anchor rows and never serialize against each other.
- **Live DB clock, read per transition.** Every transition reads `SELECT CURRENT_TIMESTAMP` (the live database server clock, the portable analogue of the PostgreSQL `clock_timestamp()` fix) once, after the serialization point is taken, and bases all expiry math on that one value. Using the DB clock, not the client's `DateTime.UtcNow`, keeps every process comparing expiries against one authoritative clock with no client-clock-skew hazard; reading it inside the transition means a hold that lapsed while the transition waited to be serialized is correctly reclaimed.
- Per-mode lease with owner-checked renew and release: a reader by its `(Resource, OwnerToken)` row, a writer by `OwnerToken` on the `'w'` row. A stale client cannot renew or release another holder's share, and releasing an already-expired share is a safe no-op.
- Best-effort cross-process writer fairness: a blocked writer plants a lease-bounded pending-writer row that holds off NEW reader arrivals (an existing reader may still refresh its own hold) so in-flight readers drain and the waiting writer proceeds. The marker carries the writer's own lease TTL, so a writer that crashes or abandons its wait cannot block readers past that TTL. Granting the writer deletes the marker. Writer-preference, not strict FIFO among writers; the exact analogue of the in-memory, Redis, and PostgreSQL pending-writer reservation.
- A serialization conflict (PostgreSQL `40001`, SQL Server deadlock `1205`) is expected and benign under same-resource contention and is retried with exponential backoff and full jitter (defaults: 64 retries, 4 ms base) so a burst of many simultaneous holders of one key drains instead of livelocking; the conflict detection is by exception type, the standard SQLSTATE classes, and the SQL Server deadlock code, kept broad so any relational provider's serialization failure retries.
- `EfCoreSharedExclusiveLockOptions` (`KeyPrefix`, `MaxSerializationRetries` defaulting to 64, `SerializationRetryBaseDelay` defaulting to 4 ms), `OrionLockRwHoldRowEntityTypeConfiguration` + `OrionLockRwResourceRowEntityTypeConfiguration` to map both tables in `OnModelCreating`, and the `OrionLockBuilder.UseEntityFrameworkCoreSharedExclusive<TDbContext>(configure?)` DI helper, which registers the provider and the composed `ISharedExclusiveLock` via `TryAdd*`. Additive to `UseEntityFrameworkCore`; both `IDistributedLock` and `ISharedExclusiveLock` can then resolve through EF Core.

#### `TryAcquireAsync` with a deadline on the exclusive lock

`IDistributedLock` gains a `TryAcquireAsync(key, deadline, ...)` overload: it polls on `RetryInterval` until the deadline and returns `null` on expiry instead of throwing `LockAcquisitionTimeoutException`, the exclusive-lock counterpart of the reader-writer deadline overloads shipped in 0.5.0. This closes the gap between the single-shot `TryAcquireAsync` and the block-or-throw `AcquireAsync` for callers that treat "could not acquire in time" as ordinary control flow. The poll delay is clamped to the time left so a `RetryInterval` larger than the deadline cannot overshoot the caller's budget. Added as a default interface method (no implementor breaks) with a concrete implementation on `DistributedLock` that mints one owner token and reuses it across every deadline retry (stable fencing identity); a non-positive deadline performs exactly one attempt, and cancellation still surfaces as `OperationCanceledException`.

### Tests

- `EfCoreSharedExclusiveLockConformance` runs the full reader-writer correctness matrix against BOTH a real PostgreSQL and a real SQL Server via Testcontainers (reusing the hardened retry-warmup container fixture pattern), proving the portability claim: the same facts pass on two different relational EF Core providers. Coverage: many readers acquire concurrently; a writer is blocked while readers are held and acquires once they release; readers are blocked while a writer is held; a second writer is blocked while one is held; an expired reader and an expired writer are each reclaimed; one reader's expiry does not free another's; renewal keeps a reader and a writer alive past their original TTL; renew and release are owner-checked so a stale token cannot affect another holder's share; release of an expired share is a no-op; the pending-writer marker blocks new readers so a continuous reader stream cannot starve a waiting writer; an existing reader may still refresh while a writer is pending; the marker itself expires so a crashed writer cannot block readers forever; and granting the writer clears the marker so a later reader still succeeds. The same matrix the Redis and PostgreSQL providers run, mirrored onto the provider-portable EF Core backend.
- `DistributedLockDeadlineTests` adds deterministic in-memory facts for the exclusive deadline overload: success when free, `null` (not throw) on the deadline while contended, success after the holder drains, single-attempt on a non-positive deadline, cancellation propagation, a no-overshoot timing guard, and a stable owner token across retries.

### Notes

The EF Core reader-writer holds tables (`OrionLock_RwHolds`, `OrionLock_RwResources`) must be created via EF Core migrations or `Database.EnsureCreated()`, as for any other application table; unlike the PostgreSQL provider there is no auto-create step, because EF Core owns the schema. Fair queueing beyond the best-effort pending-writer starvation marker (folding the FIFO coordinator into the reader-writer path) remains planned; the reader-writer providers ship the lease-bounded writer-preference marker on every backend.

## [0.5.0] - 2026-06-27

### Added

#### PostgreSQL distributed reader-writer (shared/exclusive) lock

The second distributed `ISharedExclusiveLockProvider`, and the first relational one. `PostgresSharedExclusiveLockProvider` ships in the existing `OrionLock.Postgres` package and brings the v0.4.0 reader-writer seam to PostgreSQL with the same correctness guarantees the Redis provider gives: for a given key, any number of `Shared` (read) holders coexist, OR exactly one `Exclusive` (write) holder owns it. The exclusive-only `PostgresLockProvider` (session-scoped `pg_try_advisory_lock`) and its wire format are unchanged; this is purely additive. This release supersedes the unreleased v0.4.2 Redis-only roadmap point: the family version moves to a uniform 0.5.0.

- `PostgresSharedExclusiveLockProvider` implementing `ISharedExclusiveLockProvider`. Unlike the exclusive-only advisory-lock backend, holds are modelled as clock-leased rows in a table (default `orionlock_rw_holds`): a `kind='r'` row per reader keyed by `(resource, owner_token)`, one `kind='w'` writer row, and one `kind='pw'` pending-writer row, each carrying an explicit `expires_at`. A session-scoped advisory lock cannot track readers individually, carry a per-share fencing token, or reclaim one dead reader at its own expiry, so the relational reader-writer lock needs the row model. Consequently this provider's `LeaseDurationIsTtl` is `true` (the exclusive Postgres provider's is `false`).
- Readers are tracked individually (never a bare counter), so one reader's expiry never frees another's. Expired rows are pruned by `expires_at <= now()` on every acquire, renew, and release.
- Every reader/writer transition runs in a transaction that first serializes all transitions for the key with `pg_advisory_xact_lock(hash(resource))` (xact-scoped, auto-released at commit), then prunes, evaluates the same predicates the Redis Lua scripts use, and writes. Because the advisory lock is taken before any read, the prune-evaluate-write sequence is race-free per key: there is no read-then-write window.
- All lease math uses the PostgreSQL server clock via `now()` (the transaction-start timestamp), so every process compares expiries against one authoritative clock with no client-clock-skew hazard.
- Per-mode lease with owner-checked renew and release: a reader by `(resource, owner_token)` row identity, a writer by `owner_token` equality on the `'w'` row, matching the exclusive provider's ownership discipline. A stale client cannot renew or release another holder's share, and releasing an already-expired share is a safe no-op.
- Best-effort cross-process writer fairness: a blocked writer plants a lease-bounded pending-writer row that holds off NEW reader arrivals (an existing reader may still refresh its own hold) so in-flight readers drain and the waiting writer proceeds. The marker carries the writer's own lease TTL, so a writer that crashes or abandons its wait cannot block readers past that TTL. Granting the writer deletes the marker. This is writer-preference, not strict FIFO among writers, and is the exact analogue of the in-memory and Redis pending-writer reservation.
- `PostgresSharedExclusiveLockOptions` (`KeyPrefix`, `TableName` validated as a simple SQL identifier, `AutoCreateTable` defaulting to `true` for a zero-migration first run, `CommandTimeout`) and the `OrionLockBuilder.UsePostgresSharedExclusive(connectionString, configure?)` DI helper, which registers the provider and the composed `ISharedExclusiveLock` via `TryAddSingleton`. Additive to `UsePostgres`; both `IDistributedLock` and `ISharedExclusiveLock` can then resolve against PostgreSQL.

#### `TryAcquireAsync` with a deadline on the reader-writer surface

`ISharedExclusiveLock` gains `TryAcquireSharedAsync(key, deadline, ...)` and `TryAcquireExclusiveAsync(key, deadline, ...)` overloads: they poll on `RetryInterval` until the deadline and return `null` on expiry instead of throwing `LockAcquisitionTimeoutException`. This closes the gap between the single-shot `TryAcquire*Async` and the block-or-throw `Acquire*Async` for callers that treat "could not acquire in time" as ordinary control flow rather than an exceptional condition. The poll delay is clamped to the time left so a `RetryInterval` larger than the deadline cannot overshoot the caller's budget by up to one interval, matching the blocking acquire loop. Added as default interface methods (no implementor breaks) with a concrete implementation on `SharedExclusiveLock`; a non-positive deadline performs exactly one attempt, and cancellation still surfaces as `OperationCanceledException`.

### Tests

- `PostgresSharedExclusiveLockProviderTests` runs the full reader-writer correctness matrix against a real PostgreSQL via Testcontainers (the package's established integration-test pattern, reusing the hardened retry-warmup container fixture): many readers acquire concurrently; a writer is blocked while readers are held and acquires once they release; readers are blocked while a writer is held; a second writer is blocked while one is held; an expired reader and an expired writer are each reclaimed; one reader's expiry does not free another's; renewal keeps a reader and a writer alive past their original TTL; renew and release are owner-checked so a stale token cannot affect another holder's share; release of an expired share is a no-op; the pending-writer marker blocks new readers so a continuous reader stream cannot starve a waiting writer; an existing reader may still refresh while a writer is pending; the marker itself expires so a crashed writer cannot block readers forever; and granting the writer clears the marker so a later reader still succeeds. The same matrix the Redis provider runs, mirrored onto the relational backend.
- `SharedExclusiveLockTests` adds deterministic in-memory facts for the deadline overloads: success when free, `null` (not throw) on the deadline while contended, success after the holder drains, single-attempt on a non-positive deadline, cancellation propagation, and a no-overshoot timing guard.

## [0.4.2] - 2026-06-22

### Added

#### Redis distributed reader-writer (shared/exclusive) lock

The first distributed `ISharedExclusiveLockProvider`. `RedisSharedExclusiveLockProvider` ships in the existing `OrionLock.Redis` package and brings the v0.4.0 reader-writer seam to a real cross-process backend: for a given key, any number of `Shared` (read) holders coexist, OR exactly one `Exclusive` (write) holder owns it. Every transition is a single atomic Lua script, so mutual exclusion, TTL reclaim, renewal, fencing, and writer fairness never depend on a read-then-write round-trip race. The exclusive-only `RedisLockProvider` and its wire format are unchanged; this is purely additive.

- `RedisSharedExclusiveLockProvider` implementing `ISharedExclusiveLockProvider`. Per logical key it keeps three Redis keys under its own prefix: a writer string (`:w`) holding the writer's fencing token, a readers sorted set (`:r`) whose members are reader fencing tokens scored by absolute lease-expiry, and a pending-writer string (`:pw`) holding a waiting writer's token.
- Readers are tracked individually in the sorted set, never as a bare counter, so one reader's expiry never frees another's and a leaked decrement cannot corrupt the set. Expired members are pruned by score on every acquire, renew, and release, and the set is given a matching `PEXPIRE` so a fully crashed reader fleet leaves nothing behind.
- All lease math uses the Redis server clock (`redis.call('TIME')` inside the script), so every process compares expiries against one authoritative clock with no client-clock-skew hazard. This is replication-safe under the default script-effects replication.
- Per-mode lease with owner-checked renew and release: a reader is verified by sorted-set membership, a writer by string equality on its fencing token, matching the exclusive provider's ownership discipline. A stale client cannot renew or release another holder's share, and releasing an already-expired share is a safe no-op.
- Best-effort cross-process writer fairness: when an exclusive acquire is blocked by live readers, it plants a lease-bounded pending-writer marker that holds off NEW reader arrivals (an existing reader may still refresh its own hold) so the in-flight readers drain and the waiting writer proceeds. The marker carries the writer's own lease TTL, so a writer that crashes or abandons its wait cannot block readers past that TTL. This is writer-preference, not strict FIFO ordering among competing writers, and is the distributed analogue of the in-memory pending-writer reservation. It pays off the v0.4.0 "cross-process fair ordering is a follow-up" note for the reader-writer path.
- Multi-key Lua scripts hash-tag all three keys on the logical key, so a clustered deployment routes them to one slot.
- `RedisSharedExclusiveLockOptions` (`KeyPrefix` defaulting to `orionlock:rw:`, `Database`) and the `OrionLockBuilder.UseRedisSharedExclusive(configure?)` DI helper, which registers the provider and the composed `ISharedExclusiveLock` via `TryAddSingleton` over the already-registered `IConnectionMultiplexer`. Additive to `UseRedis`; both `IDistributedLock` and `ISharedExclusiveLock` can then resolve against Redis.

### Tests

- `RedisSharedExclusiveLockProviderTests` runs the full reader-writer correctness matrix against a real Redis via Testcontainers (the package's established integration-test pattern): many readers acquire concurrently; a writer is blocked while readers are held and acquires once they release; readers are blocked while a writer is held; an expired reader and an expired writer are each reclaimed; one reader's expiry does not free another's; renewal keeps a reader and a writer alive past their original TTL; renew and release are owner-checked so a stale token cannot affect another holder's share; release of an expired share is a no-op; the pending-writer marker blocks new readers so a continuous reader stream cannot starve a waiting writer; the marker itself expires so a crashed writer cannot block readers forever; and granting the writer clears the marker so a later reader still succeeds.

### Changed

- `OrionLockDiagnostics` `ActivitySource` / `Meter` version derives from the assembly version (the v0.4.1 self-deriving `MeterVersion`), so this release needs no manual diagnostics version edit.

## [0.4.1] - 2026-06-20

### Performance

- Blocking acquire hot path no longer allocates the OpenTelemetry activity display name when no `ActivitySource` listener is subscribed. `DistributedLock.AcquireAsync` and `SharedExclusiveLock` now gate the interpolated `"OrionLock.Acquire {key}"` / `"OrionLock.AcquireRW {key}"` string behind `ActivitySource.HasListeners()`, so the per-acquire string is built only when a tracer is actually attached. With no listener (the production-typical configuration) `StartActivity` returned null and that string was never observed, so the change is behavior-identical; a subscribed listener still sees the exact same activity name. Measured on the uncontested in-memory acquire/release path: steady-state allocation dropped from about 815 to about 743 bytes per acquire (roughly 9 percent). No public API, locking, timeout, or fairness semantics changed.

## [0.4.0] - 2026-06-19

### Added

#### Shared / exclusive (reader-writer) locks

A new opt-in abstraction for reader-writer locking alongside the existing exclusive-only lock. For a given resource key, either any number of `Shared` (read) holders coexist, OR exactly one `Exclusive` (write) holder owns it. Acquire, TTL/lease, lease renewal, release, options, and diagnostics semantics mirror the exclusive `IDistributedLock`.

- `LockMode` enum (`Shared`, `Exclusive`).
- `ISharedExclusiveLock` with blocking `AcquireSharedAsync` / `AcquireExclusiveAsync` (wait + retry) and non-blocking `TryAcquireSharedAsync` / `TryAcquireExclusiveAsync`, all returning the existing `IDistributedLockHandle`.
- `ISharedExclusiveLockProvider` — the raw, single-attempt reader-writer primitive a backend implements. Kept separate from `IDistributedLockProvider` so the exclusive-only fast path and its wire format are completely unchanged.
- `SharedExclusiveLock` core composer (blocking-acquire retry loop, timeout, diagnostics, lease-renewing handles) and `SharedExclusiveLockHandle` (background renewal watchdog renewing at one third of the lease, `IsHeld` / `LostToken`, mode-aware release).
- `OrionLock.Testing` in-memory backend `InMemorySharedExclusiveLockProvider` with real lease-expiry semantics: many shared holders coexist, a writer waits for shared holders to drain, shared waits for an active writer, and TTL/expiry/release transitions are honoured. `UseInMemory()` now also registers `ISharedExclusiveLockProvider` and `ISharedExclusiveLock`.
- Writer-starvation mitigation: when an exclusive acquire is blocked by live shared holders, the in-memory backend records a lease-bounded pending-writer reservation that holds off NEW shared arrivals so the existing readers can drain. The reservation itself expires on the writer's lease, so a writer that gives up or crashes mid-wait cannot permanently block readers. This is a best-effort, in-process fairness aid; cross-process fair ordering is a v0.4.x follow-up.

The distributed backends (Redis, EntityFrameworkCore, Postgres, SqlServer, Consul, Etcd, ZooKeeper) do not implement `ISharedExclusiveLockProvider` in this release; only the in-memory testing backend ships one. A correct distributed reader-writer implementation is a documented follow-up.

### Changed

- `OrionLockDiagnostics` `ActivitySource` / `Meter` version strings bumped to 0.4.0 to match the release, per the established per-release convention.

## [0.3.32] - 2026-06-17

### Changed
- Set the NuGet package icon to the navy Moongazing mark across every sub-package, and the README logo to the white Moongazing mark.

## [0.3.31] - 2026-06-17

### Changed
- Fixed the NuGet package icon: the per-project icon assets now carry the new Moongazing mark (v0.3.30 only updated the repo-root copy, which the packages do not embed). The README logo uses the white mark.

## [0.3.30] - 2026-06-17

### Changed
- Updated the package icon and README logo to the new Moongazing mark.

## [0.3.29] - 2026-06-16

### Added

#### `orionlock.fairness.queue_depth` histogram

`Histogram<int>` records how many LIVE waiters were already ahead in the FIFO queue at the moment a new candidate entered (the depth it joined behind: 0 = it became the head with no wait). Only emitted when `UseFifoWaiterCoordinator` is enabled.

- Where the v0.3.18 `coordinator_enter_duration` measures the EFFECT (time spent waiting for the ticket), this measures the CAUSE - the concurrent contention depth. A rising depth not matched by rising `enter_duration` points at fast lock turnover; both rising together points at long hold times.
- The zero sample IS recorded: the fraction of uncontended (head-of-queue) entries is itself the signal.
- Recorded by both bundled coordinators. The in-process coordinator counts only not-yet-cancelled tickets, so a non-head waiter that cancels and lingers in the queue (until the head prunes past it) does not inflate the depth tail during a cancellation-heavy shutdown. The Redis coordinator uses the post-`ZADD` `ZRANK`, which is already live-only because every exit path `ZREM`s and the stale-prune pass runs first.
- `RecordFifoQueueDepth` is public so the Redis coordinator (a separate package) and any third-party `IFifoWaiterCoordinator` can feed the same histogram from inside their own `EnterAsync`.
- Inherits the v0.3.12 `WithMetricsLabel` static tags.

### Changed

- `OrionLockDiagnostics` `ActivitySource` / `Meter` version strings bumped to 0.3.29 to match the release, per the established per-release convention.

### Tests

- `FifoQueueDepthTests`: the helper emits the value and clamps negatives; `EnterAsync` records depth 0 for the head and depth 1 for the next candidate; a cancelled-but-lingering waiter is excluded from a later candidate's recorded depth.
- `RedisFifoWaiterCoordinatorTests`: `EnterAsync` records the live `ZRANK` depth each caller joins behind.

## [0.3.28] - 2026-06-15

### Added

#### `orionlock.acquire.cancelled` counter

`Counter<long>` increments when `DistributedLock.AcquireAsync` is abandoned via the caller's `CancellationToken` (a graceful shutdown, or a client that gave up) rather than by exceeding `WaitTimeout`.

- Distinct from the v0.3.16 `acquire.timeout` counter: a timeout is a contention-SLO breach an operator may alert on, whereas a cancellation is usually expected (deployments, request aborts). Separating them keeps the timeout signal clean for alerting.
- Recorded in a `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)` on the acquire loop; the cancellation then propagates unchanged. The activity is tagged `outcome=cancelled`.
- Inherits the v0.3.12 `WithMetricsLabel` static tags.

### Changed

- `OrionLockDiagnostics` `ActivitySource` / `Meter` version strings bumped to 0.3.28 to match the release, per the established per-release convention.

### Tests

- `AcquireCancelledCounterTests`: a cancelled acquire against an always-busy backend increments the counter and propagates the cancellation; the helper increments the counter.

## [0.3.27] - 2026-06-15

### Added

#### `orionlock.handle.renewals_per_hold` histogram

`Histogram<int>` records how many successful lease renewals each handle performed during its hold, emitted once at release/loss alongside the v0.3.14 `holding_duration`. Where `holding_duration` is wall-clock, this is the discrete count of background renewal round-trips a lock cost the backend; the two diverge when renewals fail (the v0.3.19 failure-streak histogram) or the lease interval jitters.

- Operators graph p99 to find critical sections that hold a lock across many renewal cycles (lock-hold hot spots) and to size renewal load on the lock backend.
- The zero sample IS recorded: a short hold or an `AutoRenew`-off handle legitimately renews zero times, and the fraction of zero-renewal holds is itself the signal.
- Emitted exactly once per handle via a dedicated single-fire `EmitRenewalsPerHoldOnce`: the watchdog-loss path emits at surrender (its count is final), and the dispose path emits only AFTER the watchdog is cancelled and awaited, so a handle disposed mid-renewal cannot under-report the last renewal by one (codex/CodeRabbit P2). The counter is incremented under `Interlocked` by the watchdog and read with `Volatile.Read`.
- Tags inherited from the v0.3.12 `WithMetricsLabel` static-tag set.
- `OrionLockDiagnostics` `ActivitySource` / `Meter` version strings bumped to 0.3.27 to match the release, per the established per-release convention (CodeRabbit).

### Tests

- `RenewalsPerHoldTests`: an `AutoRenew`-off hold records 0 on dispose; the helper emits the count and clamps a negative to 0.

## [0.3.26] - 2026-06-13

### Added

#### `orionlock.reentrancy.max_depth` histogram

`Histogram<int>` of the DEEPEST reentrancy depth reached per hold lifetime (the high-water mark of nested re-acquisitions of the same key before the outermost handle is disposed). Distinct from the v0.3.17 `reentrancy.depth` gauge which shows the instantaneous outstanding count; this histogram's p99 reveals how deep real-world re-entry actually goes - helping operators spot accidental deep recursion that re-acquires a lock it already holds.

- A hold with no re-entry emits a sample of 1 (full distribution visible, not just outliers).
- Recorded once on the final (outermost) Exit.
- Inherits v0.3.12 `WithMetricsLabel` static tags.

### Tests

2 facts.

### Migration from v0.3.25

Source-compatible.

## [0.3.25] - 2026-06-12

### Added

#### `ILockEventObserver` emission-site wire-up

The v0.3.24 contract is now fully wired:

- `DistributedLock` gains a 3-arg ctor accepting the optional observer; fires `OnAcquired` (success path of `AcquireAsync`) and `OnAcquireTimedOut` (before the timeout throw).
- `DistributedLockHandle` gains a 5-arg public ctor; fires `OnLeaseLost` on both surrender paths (backend-confirmed loss AND grace-exhausted watchdog surrender) and `OnReleased` on normal `DisposeAsync` while still held.
- `AddOrionLock` resolves `ILockEventObserver` from DI explicitly (`sp.GetService<ILockEventObserver>()`), so `services.AddSingleton<ILockEventObserver, MyObserver>()` now works as documented.
- All invocations go through safe-invoke wrappers that swallow observer faults - audit-side outages cannot disrupt the lock path.
- Watchdog-loss and dispose paths cannot double-fire: `OnReleased` only fires while `isHeld` is still true, and loss paths flip `isHeld` before dispose runs.

### Tests

2 integration facts (DI-registered observer receives acquired + released; timeout fires OnAcquireTimedOut under real contention).

### Migration from v0.3.24

Source-compatible. Observers registered in v0.3.24 (no-op then) start receiving callbacks after this upgrade.

## [0.3.24] - 2026-06-12

### Added

#### `ILockEventObserver` extensibility

Consumer-supplied observer for lock lifecycle events. Useful for application audit trails of distributed lock acquisition (compliance / incident triage) without coupling the audit logic to the load-bearing acquire/release path.

- `ILockEventObserver` interface with `OnAcquired`, `OnAcquireTimedOut`, `OnLeaseLost`, `OnReleased` callbacks.
- `NullLockEventObserver` default.
- The contract is the same null-or-Null convention used by v0.2.18+ Patch / Vault / Guard observers: `null` and `NullLockEventObserver` both treated as 'no observer'.

### Tests

2 facts.

### Migration from v0.3.23

Source-compatible.

## [0.3.23] - 2026-06-12

### Added

#### `key_hash` cardinality-bucketed tag on `orionlock.acquire.timeout`

The `orionlock.acquire.timeout` counter now emits with a `key_hash` tag (64 buckets) so operators can run `topk(10, sum by (key_hash)(orionlock_acquire_timeout_total))` to find the buckets driving the most timeouts without exploding metric cardinality on raw key strings (a multi-tenant deployment may have millions of unique keys).

- Uses FNV-1a hash over UTF-16 chars - cheap, deterministic across processes (`string.GetHashCode` is randomized per AppDomain).
- Public `OrionLockDiagnostics.HashKeyToBucket(string)` so dashboards / log lines can compute the same bucket.
- Inherits v0.3.12 `WithMetricsLabel` static tags.

### Tests

4 facts.

### Migration from v0.3.22

Source-compatible.

## [0.3.22] - 2026-06-11

### Added

#### `orionlock.acquire.attempt_count` histogram

`Histogram<int>` of `TryAcquireAsync` attempts per `AcquireAsync` call (successful acquires only). Operators graph p99 to size `RetryInterval` against actual contention shape:

- Many attempts but quick acquire = polling too aggressively; raise `RetryInterval` to relieve backend.
- Few attempts but slow acquire = polling cadence is fine but backend / FIFO queue is slow.

Emits only when the acquire eventually succeeded so cancelled or timed-out paths do not pollute the distribution. Inherits v0.3.12 `WithMetricsLabel` static tags.

### Tests

2 facts.

### Migration from v0.3.21

Source-compatible.

## [0.3.21] - 2026-06-11

### Added

#### `orionlock.lease.expired_before_release` counter

`Counter<long>` increments when `DisposeAsync` runs but the handle's lease wall clock has already elapsed since the last successful renewal. Distinct from `orionlock.lease.lost` (confirmed backend-side loss via renewal returning false). Operators graph the rate to spot 'holders too slow for the configured LeaseDuration' situations the lost counter alone cannot diagnose.

- Inherits v0.3.12 `WithMetricsLabel` static tags.
- Recorded BEFORE the dispose mutates `isHeld` so the staleness check reads a coherent state.

### Tests

1 fact.

### Migration from v0.3.20

Source-compatible.

## [0.3.20] - 2026-06-11

### Added

#### `orionlock.health.last_check_at_unix_seconds` ObservableGauge

Gauge of the Unix seconds at which the OrionLock health check last completed. `0` until the first run; operators query `(now() - last_check_at) > N` to flag a stuck check loop separately from a backend that is actually unhealthy.

- Recorded on every backend-side completion path (healthy, degraded, unhealthy, backend failure). NOT recorded on caller-driven cancellation (same semantics as the existing health-check result counter).
- Atomic `Interlocked.Exchange` write prevents torn reads.

### Tests

1 fact.

### Migration from v0.3.19

Source-compatible.

## [0.3.19] - 2026-06-11

### Added

#### `orionlock.lease.renewal_failures_consecutive` histogram

`Histogram<int>` of consecutive renewal failures observed per handle before the handle either recovers (success after N failures) or surrenders (lease lost / grace exhausted). Operators graph p99 to size `RenewalFailureGracePeriod` against actual backend flakiness rather than guessing.

- Recorded on three paths: successful renewal after a failure streak (recovery), lease-lost surrender, grace-exhausted surrender.
- Zero/negative inputs are ignored at the helper level.
- Inherits v0.3.12 `WithMetricsLabel` static tags.

### Tests

2 facts.

### Migration from v0.3.18

Source-compatible.

## [0.3.18] - 2026-06-11

### Added

#### `orionlock.fairness.coordinator_enter_duration` histogram

`Histogram<double>` exposes the wait time for a FIFO ticket before entering the contention loop. Only fires when `UseFifoWaiterCoordinator` is on. Operators graph p99 to spot head-of-line blocks in the FIFO queue.

- Inherits v0.3.12 `WithMetricsLabel` static tags.
- Isolated from `acquire.duration` / `contention.duration` so operators can distinguish FIFO queueing latency from backend acquisition latency.

### Tests

1 fact.

### Migration from v0.3.17

Source-compatible.

## [0.3.17] - 2026-06-11

### Added

#### `orionlock.reentrancy.depth` UpDownCounter

Gauge of how many nested reentrant handles this process currently holds across all keys. Operators graph alongside `orionlock.leases.held_concurrent` to spot nested-call patterns that look like simple long holds in the leases gauge alone.

- Only NESTED re-entries are counted; the outermost acquire does not move the gauge.
- `ReentrancyRegistry.TryEnter` increments per nested entry; the non-terminal `Exit` decrements; the terminal `Exit` (count = 0) does NOT decrement (the outermost was never incremented).
- Inherits v0.3.12 `WithMetricsLabel` static tags.

### Tests

1 fact.

### Migration from v0.3.16

Source-compatible.

## [0.3.16] - 2026-06-11

### Added

#### `orionlock.acquire.timeout` counter

`Counter<long>` that increments each time `DistributedLock.AcquireAsync` throws `LockAcquisitionTimeoutException` because the contention loop exceeded `WaitTimeout`. Distinct from `orionlock.contentions` which counts EVERY contended `TryAcquireAsync` miss; this counter only fires when the caller gave up.

- Emitted inside `AcquireAsync` before the throw so a panicking caller cannot miss the metric.
- Inherits v0.3.12 `WithMetricsLabel` static tags via the `RecordAcquireTimeout` helper.
- Pairs with `acquire.duration` (success p99) and `contention.duration` (contended p99) to give operators a three-way view: how often timeouts happen, how long contended waits take, how long successful acquires take.

### Tests

2 new facts.

### Migration from v0.3.15

Source-compatible.

## [0.3.15] - 2026-06-11

### Added

#### `orionlock.contention.duration` histogram

`Histogram<double>` exposing how long contended acquires spent waiting. Only contended attempts (those that hit at least one `TryAcquireAsync` miss before success) emit so the histogram tail is not diluted by the zero-contention happy path.

- Recorded inside `DistributedLock.AcquireAsync` after the eventual handle is produced, gated by a local `contended` flag.
- Distinct from the existing `orionlock.acquire.duration` histogram which records EVERY successful acquire; together they answer "what is the contention pressure" vs "what is the steady-state acquire latency".
- Inherits v0.3.12 `WithMetricsLabel` static tags via the `RecordContentionDuration` helper.

### Tests

2 new facts.

### Migration from v0.3.14

Source-compatible.

## [0.3.14] - 2026-06-11

### Added

#### `orionlock.handle.holding_duration` histogram

`Histogram<double>` exposing the distribution of how long each lease was held between acquire and dispose. Pairs with the v0.3.13 `held_concurrent` gauge to answer "are leases held briefly or for an unusually long time?" - the gauge alone cannot distinguish steady churn from a stuck holder.

- Emitted in `DistributedLockHandle.DecrementOnceIfHeld`, so both normal dispose AND watchdog-loss paths produce a sample.
- Stopwatch ticks (`GetElapsedTime`) used instead of `DateTime.UtcNow` so clock adjustment during a long hold cannot skew the measurement.
- Inherits v0.3.12 `WithMetricsLabel` static tags.
- Exactly-once via the same Interlocked guard as the decrement: dispose-twice or watchdog-loss-then-dispose still record only ONE sample.

### Tests

2 new facts.

### Migration from v0.3.13

Source-compatible.

## [0.3.13] - 2026-06-11

### Added

#### `orionlock.leases.held_concurrent` gauge

UpDownCounter that operators graph to see in real time how many leases this process currently holds. Useful for spotting handle leaks, holds-longer-than-expected, or load concentration on a small set of keys.

- Increment in `DistributedLock.AcquireAsync` (success path).
- Decrement in `DistributedLockHandle.DisposeAsync` AND in both fairness-watchdog loss paths (grace-period exhausted, single-renewal failure).
- Exactly-once guarantee via Interlocked.Exchange on a per-handle `decremented` flag - dispose-after-loss or dispose-twice still nets to zero.
- Inherits static metrics tags from v0.3.12 `WithMetricsLabel`.

### Tests

2 new facts.

### Migration from v0.3.12

Source-compatible.

## [0.3.12] - 2026-06-11

### Added

#### `OrionLockBuilder.WithMetricsLabel` static metrics tags

Multi-tenant deployments running OrionLock across several tenants on one host need a way to split dashboards by tenant / region / shard without registering a separate `Meter`. v0.3.12 adds a static-tag stamping hook that gets applied to every counter the library emits.

- `OrionLockBuilder.WithMetricsLabel(string key, string value)` adds one tag.
- `OrionLockBuilder.WithMetricsLabels(IReadOnlyDictionary<string, string> tags)` adds many; later keys override earlier ones.
- Tags stamp on `orionlock.acquisitions`, `orionlock.contentions`, `orionlock.lease.lost`, `orionlock.lease.grace_period_exhausted` (the per-backend tag on `acquire.latency` / `lease_renewal.duration` / `lease_renewal.failures` is preserved unchanged - the static tag is ADDED alongside).
- Mutation is single-threaded at startup; the tag array is snapshotted into a single field that emission sites read atomically.

### Tests

4 new facts.

### Migration from v0.3.11

Source-compatible.

## [0.3.11] - 2026-06-11

### Added

#### `orionlock.lease.grace_period_exhausted` fairness metric

Extends the v0.3.10 fairness watchdog. v0.3.10 incremented `orionlock.lease.lost` on both confirmed losses and fairness auto-releases - dashboards could not distinguish a healthy lease expiry (the backend returned `false` from TryRenew) from a stuck-backend release (the watchdog gave up after the grace period). v0.3.11 splits them:

- `orionlock.lease.lost` still counts ALL confirmed losses (including the fairness path so existing alerts continue firing).
- `orionlock.lease.grace_period_exhausted` is the NEW counter that increments ONLY when the fairness watchdog surrenders due to renewal grace period exhaustion.

A spike in `grace_period_exhausted` signals backend instability, while a steady `lost` rate without `grace_period_exhausted` is normal lease churn.

### Tests

1 new fact verifying both counters increment via a `MeterListener`.

### Migration from v0.3.10

Source-compatible.

## [0.3.10] - 2026-06-11

### Added

#### Fairness watchdog: auto-release on prolonged renewal failure

v0.3.9 and earlier kept retrying lease renewals INDEFINITELY on transient backend exceptions - the only way a held lock surrendered was a renewal that explicitly returned `false`. A stuck backend (unreachable for hours but never returning a clean `false`) could perpetually deny new waiters because the holder never thought its lease was lost.

- `DistributedLockOptions.RenewalFailureGracePeriod` (nullable; default = `LeaseDuration`) bounds how long the watchdog tolerates throwing renewals before declaring the lease lost.
- When the grace period elapses since the last successful renewal AND a renewal throws again, the watchdog flips `IsHeld` to false, increments the `orionlock.lease.lost` counter, and trips `LostToken` so the consumer can react.
- Successful renewal updates the `lastSuccessfulRenewalUtc` timestamp so the grace period resets - intermittent transient faults under the cap continue retrying as before.
- Internal `DistributedLockHandle` ctor exposes a `nowUtc` clock hook so tests can drive the deadline deterministically.

### Tests

2 new facts: `LostToken` fires when renew failures exceed grace, successful renewal resets the grace window. 13 facts total.

### Migration from v0.3.9

Source-compatible. Defaulting `RenewalFailureGracePeriod` to `LeaseDuration` means a held lock that loses contact with the backend will declare itself lost after `LeaseDuration` of failures - matching the backend lease TTL contract.

## [0.3.9] - 2026-06-11

### Added

#### `IDistributedLockProvider.WaitForAcquireAsync` polling helper

Composes blocking-acquire semantics on top of the single-shot `TryAcquireAsync` primitive without forcing every backend to ship its own polling loop.

- `DistributedLockProviderExtensions.WaitForAcquireAsync(provider, key, owner, lease, acquireTimeout, options?, ct)`.
- Exponential backoff with jitter: `random(InitialDelay, InitialDelay * 2^attempts)` capped at `MaxDelay`. Default 25 ms initial, 2 s cap. Reduces thundering-herd when many waiters race for the same key.
- Never sleeps past the deadline.
- `Timeout.InfiniteTimeSpan` blocks until acquired or cancellation.
- Returns `false` on timeout; `OperationCanceledException` on cancellation.
- `WaitForAcquireOptions` with `InitialDelay`, `MaxDelay`, `RandomFactory` (for seeded testing).

### Tests

7 new facts.

### Migration from v0.3.8

Source-compatible.

## [0.3.8] - 2026-06-10

### Added

#### ZooKeeper SASL / digest ACL factory

Closes the v0.3.7 deferral.

- `IZooKeeperAclFactory` abstraction.
- `OpenZooKeeperAclFactory` default - preserves v0.3.7 `OPEN_ACL_UNSAFE`.
- `DigestZooKeeperAclFactory` - parent CREATE+READ (0x3), child CRDA+WRITE (0x1F), pre-computed `base64(sha1(user:pass))`.
- `DefaultZooKeeperClientAdapter` 2-arg ctor (1-arg retained for ABI compat).
- `OrionLockBuilder.UseDigestAcl(username, password)` DI helper.

### Tests

5 new facts; 17 total.

### Migration from v0.3.7

Source-compatible.

## [0.3.7] - 2026-06-10

### Added

#### `Moongazing.OrionLock.ZooKeeper` (NEW PACKAGE) - Apache ZooKeeper backend

Fifth distributed-lock provider. Implements the canonical ZooKeeper distributed-lock recipe: ephemeral-sequential child znodes under a per-key parent. The holder is the child with the lowest sequence number.

- **`ZooKeeperLockProvider`** implements `IDistributedLockProvider`. `TryAcquireAsync` creates an `EPHEMERAL_SEQUENTIAL` child under the lock-key parent znode (`/orionlock/{key}`) and declares ownership when it holds the lowest sequence; loses the race and self-deletes when another child has lower sequence. `(ownerToken, key)` pair tracking so the same owner can hold multiple keys safely.
- **Session-expiry contract**: ZooKeeper deletes ephemeral znodes when their owning session closes (process crash, network partition past session timeout). OrionLock therefore inherits the broker's liveness guarantees without a TTL of its own; a crashed holder loses the lock the moment the session expires. `TryRenewAsync` is a liveness check (does the znode still exist) rather than a TTL extension because the ZooKeeper client owns the heartbeat.
- **`ZooKeeperLockOptions`** carries `RootPath` (default `/orionlock`) with `ValidateAndNormalise()` that rejects an empty path and adds a leading slash if the consumer forgot it.
- **`IZooKeeperClientAdapter`** abstraction over `EnsurePath` / `CreateEphemeralSequential` / `GetChildren` / `Delete` / `Exists`. Production wires `DefaultZooKeeperClientAdapter` over the official `ZooKeeperNetEx` client; unit tests substitute mocks so the provider can be exercised without a running ZooKeeper ensemble.
- **`OrionLockBuilder.UseZooKeeper(configure?)`** DI helper. Consumers register the `ZooKeeper` client themselves because the connection requires a `Watcher` instance for session-state callbacks - that contract belongs to the consumer.

### Tests

11 unit facts cover: acquire when child is lowest sequence, lose race when another child has lower sequence (orphan self-delete), TryRenew without active session, TryRenew when znode still exists, TryRenew drops mapping when znode gone (no double adapter hit), Release deletes child, Release idempotent for unknown owner-key pair, Release swallows delete exception (ephemeral auto-cleans on session close), RootPath namespacing, options validation, RootPath normalisation.

### Migration from v0.3.6

Source-compatible. Add-on is opt-in:

```csharp
services.AddSingleton<ZooKeeper>(_ => new ZooKeeper("localhost:2181", 30_000, new MyWatcher()));
services.AddOrionLock().UseZooKeeper();
```

## [0.3.6] - 2026-06-10

### Added

#### `Moongazing.OrionLock.Etcd` (NEW PACKAGE) - etcd v3 backend

Fourth distributed-lock provider. etcd v3 lease-bound keys: `TryAcquire` creates a lease + transactional put-if-absent against the key, `TryRenew` pings the lease keep-alive, `Release` performs delete-if-match then revokes the lease.

- **`EtcdLockProvider`** implements `IDistributedLockProvider`. `(ownerToken, key)` pair tracking (same protective pattern as the Consul provider) so the same owner can hold multiple keys safely.
- **Lease-expiry contract**: etcd automatically removes the key when the lease TTL elapses without a keep-alive, so a crashed holder loses the lock without external intervention. Other instances see the key free on the next polling tick.
- **Compare-and-swap on release**: `KvDeleteIfMatchAsync` guards against the lease-expiry race where another owner took over the key - the holder MUST NOT delete the new owner's key. The lease revoke runs unconditionally so the slot does not leak on etcd.
- **Session-leak protection**: KV-put failure between lease grant and dictionary store triggers an explicit lease revoke before re-throwing.
- **`EtcdLockOptions`** carries `KeyPrefix` (default `orionlock/`) and `MinLeaseTtlSeconds` (default 5; etcd lease TTL is integer seconds).
- **`IEtcdClientAdapter`** abstraction over `LeaseGrant`/`LeaseKeepAlive`/`LeaseRevoke`/`KvPutIfAbsent`/`KvDeleteIfMatch`. Production wires `DefaultEtcdClientAdapter` over the official `dotnet-etcd` client; unit tests substitute mocks so the provider can be exercised without a running etcd cluster.
- **`OrionLockBuilder.UseEtcd(connectionString, configure?)`** + **`UseEtcd(configure?)`** DI helpers. The connection-string overload uses `AddSingleton` (not `TryAddSingleton`) so the supplied address wins over any previously-registered `IEtcdClient`.

### Tests

11 unit facts cover: lease grant + KV put, orphan-lease revoke on lost race, orphan-lease revoke on KV-put exception, `MinLeaseTtlSeconds` clamp, TryRenew without active lease, TryRenew on healthy lease, TryRenew dropping mapping on lease-lost, Release delete-if-match + revoke, Release idempotent for unknown owner-key pair, multi-key isolation under same owner, KeyPrefix namespacing.

### Migration from v0.3.5

Source-compatible. Add-on is opt-in:

```csharp
services.AddOrionLock()
    .UseEtcd("http://localhost:2379");
```

## [0.3.5] - 2026-06-10

### Added

#### `Moongazing.OrionLock.Consul` (NEW PACKAGE) - HashiCorp Consul backend

Third distributed-lock provider. Lands the v0.3.4 deferral.

- **`ConsulLockProvider`** implements `IDistributedLockProvider` over Consul session-bound KV acquire/release semantics. Each (lockKey, ownerToken) pair gets a Consul session whose TTL is the OrionLock lease duration; `TryAcquireAsync` issues a session-scoped KV acquire, `TryRenewAsync` renews the session, `ReleaseAsync` releases the KV and destroys the session.
- **Session expiry contract**: when the holder process crashes, Consul's session TTL elapses and applies the configured behaviour (default `release` - the key returns to the pool so blocking waiters see it free on the next polling tick). `delete` is also supported.
- **`ConsulLockOptions`** carries `KeyPrefix` (default `orionlock/`), `SessionBehavior` (default `release`), and `MinSessionTtl` (default 10 s - Consul rejects shorter TTLs; the provider takes `max(LeaseDuration, MinSessionTtl)`).
- **`IConsulClientAdapter`** abstraction over the subset of Consul KV / Session operations OrionLock needs. Production wires `DefaultConsulClientAdapter` over the official Consul.NET `IConsulClient`; tests substitute mocks so unit tests don't need a running Consul.
- **`OrionLockBuilder.UseConsul(address, configure?)`** and **`UseConsul(configure?)`** (over an already-registered `IConsulClient`) DI helpers. Replaces any previously-registered `IDistributedLockProvider`.

### Tests

10 unit facts covering: session create + KV acquire, orphan-session cleanup on lost race, MinSessionTtl floor, TryRenew without active session, TryRenew on healthy session, TryRenew dropping mapping on Consul-side session expiry (and not hitting the adapter again), Release issuing both KV release and session destroy, Release idempotent for unknown owner, `delete` behaviour flowing through, key-prefix namespacing.

### Migration from v0.3.4

Source-compatible. Add-on is opt-in:

```csharp
services.AddOrionLock()
    .UseConsul("http://localhost:8500");
```

## [0.3.4] - 2026-06-09

### Added

#### `RedisFifoWaiterCoordinator` - distributed cross-process FIFO

The first distributed `IFifoWaiterCoordinator` implementation. Pairs with the v0.3.3 wiring so consumers opt blocking acquires into cross-process arrival-order fairness without changing any other lock backend.

- **`RedisFifoWaiterCoordinator`** ships in the existing `Moongazing.OrionLock.Redis` package. One Redis sorted set per lock key: member = unique waiter id (Guid), score = arrival epoch millisecond. `EnterAsync` ZADD + polls `ZRANGE 0 0` until head; `LeaveAsync` ZREM. Cancellation removes the caller from the queue so it does not block waiters behind it.
- **`RedisFifoWaiterOptions`** carries `Database`, `KeyPrefix` (default `orionlock:fifo`), `PollInterval` (default 50 ms), `WaiterTtl` (default 5 min). A scan-and-prune pass runs on every Enter / Leave call to remove stale entries by score (crashed process).
- **`OrionLockBuilder.UseRedisFifoWaiterCoordinator(configure?)`** registers the coordinator as singleton over the already-resolved `IConnectionMultiplexer`. Replaces any previously-registered coordinator (the default `NullFifoWaiterCoordinator` wired by `AddOrionLock`). Consumers still opt in per-acquire via `DistributedLockOptions.UseFifoWaiterCoordinator = true`.
- `OrionLockDiagnostics.ActivitySource` and `Meter` versions bumped to `0.3.4`.

### Deferred

- `OrionLock.Consul` backend -> v0.3.5 (unchanged)

### Migration from v0.3.3

Source-compatible. Default behaviour unchanged.

```csharp
services.AddOrionLock()
    .UseRedis("localhost:6379")
    .UseRedisFifoWaiterCoordinator();

await lock.AcquireAsync(key, new DistributedLockOptions
{
    UseFifoWaiterCoordinator = true,
});
```

## [0.3.3] - 2026-06-09

### Added

#### FIFO waiter coordinator wiring into `AcquireAsync`

Completes the v0.3.2 contract by integrating `IFifoWaiterCoordinator` into the blocking-acquire retry loop. Consumers who registered an alternate coordinator in v0.3.2 saw no behaviour change yet; v0.3.3 enables it via an explicit opt-in.

- **`DistributedLockOptions.UseFifoWaiterCoordinator`** new bool, default `false`. When `true`, `AcquireAsync(key, options)` consults the registered coordinator before entering the polling-retry loop and releases the ticket in a `finally` so timeouts and cancellations do not strand subsequent waiters.
- **`DistributedLock` constructor** gains an optional `IFifoWaiterCoordinator` parameter (defaulting to `NullFifoWaiterCoordinator`). Positional callers of the v0.3.2 single-arg constructor still compile.
- **DI**: `AddOrionLock()` now `TryAddSingleton<IFifoWaiterCoordinator, NullFifoWaiterCoordinator>()`, so consumers opt in by registering `InProcessFifoWaiterCoordinator` (or a distributed implementation) before that call.
- **Non-blocking path unchanged**: `TryAcquireAsync` deliberately bypasses the coordinator. Opt-in fairness applies only to the blocking `AcquireAsync` contract.
- `OrionLockDiagnostics.ActivitySource` and `Meter` versions bumped to `0.3.3`.

### Deferred

- Distributed (cross-process) FIFO backend (Redis sorted-set queueing) -> v0.3.4 (renamed from v0.3.4 Consul which slides to v0.3.5)
- `OrionLock.Consul` backend -> v0.3.5 (was v0.3.4)

### Migration from v0.3.2

Source-compatible. Default behaviour unchanged. Opt in per acquire:

```csharp
services.AddSingleton<IFifoWaiterCoordinator, InProcessFifoWaiterCoordinator>();
services.AddOrionLock().UseRedis(/* ... */);

await lock.AcquireAsync(key, new DistributedLockOptions
{
    UseFifoWaiterCoordinator = true,
});
```

## [0.3.2] - 2026-06-09

### Added

#### Optional FIFO waiter coordination (contract + in-process implementation)

- **`IFifoWaiterCoordinator`** in `Moongazing.OrionLock.Fairness`. Two methods: `EnterAsync(key, ct)` returns an `IFifoWaiterTicket` when the caller reaches the head of the per-key queue; `LeaveAsync(ticket, ct)` pops the head so the next waiter unblocks. Catches the "thundering herd attempts AcquireAsync, last-arrived first-served polling race" failure mode without forcing every consumer to write their own coordination.
- **`NullFifoWaiterCoordinator`** default registration. Every `EnterAsync` completes immediately with a no-op ticket - byte-for-byte v0.3.1 behaviour, so consumers see no change unless they wire an alternate implementation.
- **`InProcessFifoWaiterCoordinator`** single-process implementation backed by a `ConcurrentDictionary<string, Queue<TaskCompletionSource>>`. Tickets are issued and dequeued in arrival order per key; cancelled non-head waiters self-skip on dequeue so a cancellation does not stall the queue. Different keys do not contend.
- `OrionLockDiagnostics.ActivitySource` and `Meter` versions bumped to `0.3.2`.

### Deferred to v0.3.3

- Wiring `IFifoWaiterCoordinator` into `DistributedLockOptions` and the `AcquireAsync` retry loop. v0.3.2 ships the contract + implementation so distributed (cross-process) backends - Redis sorted-set queueing, ZooKeeper ephemeral nodes - can land in v0.3.3 without changing the public interface.
- `OrionLock.Consul` backend continues to target v0.3.4 (renamed from v0.3.3 because the FIFO integration takes that slot).

`ROADMAP.md` reflects the new sequence.

### Migration from v0.3.1

Source-compatible. The new interface and implementations are additive; no DI changes are required to stay on v0.3.1 behaviour. Pre-register an alternate coordinator if you want to start measuring the contract today:

```csharp
services.AddSingleton<IFifoWaiterCoordinator, InProcessFifoWaiterCoordinator>();
```

The default `AcquireAsync` retry path does not yet consult the coordinator; integration ships in v0.3.3.

## [0.3.1] - 2026-06-04

### Added

#### Lease-renewal failure telemetry

- New `orionlock.lease_renewal.failures` counter on the existing `Moongazing.OrionLock` Meter, tagged with `backend`. Distinct from `orionlock.lease.lost`: a renewal call that throws (transient network failure, backend timeout) before the watchdog can confirm the result is recorded as a *failure*; a renewal call that returns false (lease confirmed gone, peer took it, lease expired) continues to record as a *loss*. Lets operators tell "backend is unstable but the lease is still ours" apart from "we lost the lease".
- `MeasuringLockProvider.TryRenewAsync` records the failure counter and re-throws so the watchdog observes the original exception unchanged. The watchdog's catch now only treats the throw as renewed=false and lets the next renewal interval run.
- `OrionLockDiagnostics.ActivitySource` and `Meter` versions bumped to `0.3.1`.

### Deferred

Remaining v0.3.x items from the original 0.3.0 plan continue with their previously published targets:

- Optional FIFO waiter queueing -> v0.3.2.
- `OrionLock.Consul` backend -> v0.3.3.

### Migration from v0.3.0

Source-compatible. No DI registration changes. The new counter starts emitting on adopt without configuration.

## [0.3.0] - 2026-06-01

### Added

- New package `Moongazing.OrionLock.HealthChecks` 0.3.0 with `AddOrionLockHealthCheck(...)` on `IHealthChecksBuilder`. The check probes the registered `IDistributedLockProvider` by acquiring and releasing a configurable sentinel key (`OrionLockHealthCheckOptions.SentinelKey`, default `orionlock:_healthcheck`) with a short lease (default 2 s) and `WaitTimeout` (default 500 ms). Returns `Healthy` on success, `Degraded` on `LockAcquisitionTimeoutException` or sentinel contention, `Unhealthy` on `OrionLockBackendException` or any other exception with the message surfaced in `Data["error"]`. Intended for container readiness probes that should fail fast when the lock backend is unreachable.
- Three new instruments on the existing `Moongazing.OrionLock` Meter:
  - `orionlock.acquire.latency` histogram (milliseconds), tagged with `backend` (`redis`, `sqlserver`, `postgres`, `efcore`, `inmemory`). Measures a single backend `TryAcquireAsync` round-trip and isolates backend latency from the blocking-retry loop that `orionlock.acquire.duration` covers.
  - `orionlock.lease_renewal.duration` histogram (milliseconds), tagged with `backend`. Wired into the renewal watchdog so spikes that risk pushing renewals past `LeaseDuration / 3` are observable.
  - `orionlock.health_check.result` counter, tagged with `result` (`healthy`, `degraded`, `unhealthy`), incremented on every health-check probe.
- New `BackendNameAttribute` and `BackendNameResolver` in `Moongazing.OrionLock.Diagnostics` so providers declare their telemetry identifier as a stable class-level constant. All five bundled providers are annotated.
- Lock-key cardinality guidance documented at [docs/lock-key-cardinality.md](docs/lock-key-cardinality.md). Lock keys are never used as metric tags by OrionLock; if you wrap OrionLock, follow the same rule.

### Changed

- `AddOrionLock` now transparently wraps the registered `IDistributedLockProvider` in an internal measuring decorator at `IDistributedLock` construction time. The decorator records `orionlock.acquire.latency` and `orionlock.lease_renewal.duration` and passes provider exceptions and return values through unchanged. No public-API change for consumers; backends do not need to opt in.

### Deferred from v0.3.0

- **Optional FIFO waiter queueing for `AcquireAsync`** is deferred to v0.3.1. The blocking-acquire semantics change deserves its own design cycle and a behaviour-change opt-in switch.
- **`OrionLock.Consul` backend** is deferred to v0.3.2. Third-party SDK integration belongs in its own release alongside the matching design notes.

### Migration

No breaking changes. The HealthChecks package is opt-in - existing applications continue to work unchanged. The three new instruments are emitted automatically on the existing `Moongazing.OrionLock` Meter; consumers already listening on that Meter pick them up without configuration. The internal measuring decorator does not alter `IDistributedLockProvider` semantics or surface.

## [0.2.3] - 2026-05-26

### Added

- New backend package `OrionLock.Postgres` 0.2.3 using PostgreSQL `pg_try_advisory_lock` for session-scoped advisory locks. Same semantics as `OrionLock.SqlServer`: lock lifetime is the session lifetime; crashed process auto-releases. Configurable `KeyPrefix` and `CommandTimeout`. String keys hashed via SHA-256 to a 64-bit integer (Postgres advisory keys are int64). `Dispose` explicitly calls `pg_advisory_unlock` before returning each held connection to the Npgsql pool, so locks drop deterministically on provider disposal.

## [0.2.2] - 2026-05-26

### Fixed

- Packaged logo is now actually the cream-bg version. v0.2.1 shipped the per-csproj copy of the old transparent logo because csproj `<None Include="docs/logo.png">` resolves relative to the csproj, not the repo root. Per-csproj copies are now synced to the cream-bg root file. No functional change.

## [0.2.1] - 2026-05-26

### Changed

- Logo now ships with a cream (#F7F1E3) background instead of transparent. Improves contrast against dark-mode README rendering and NuGet package card backgrounds. No functional change.

## [0.2.0] - 2026-05-24

### Added

- `OrionLock.SqlServer` backend using native `sp_getapplock` with session-scope
  lifetime. The lock is held while the SQL session is alive — a crashed process
  releases its locks automatically, with no clock-based expiry. `KeyPrefix` and
  `CommandTimeout` options; combined key length limit of 240 characters
  (SQL Server `@Resource` is `nvarchar(255)` with a 15-char safety margin).
- `OrionLockBackendException` for non-contention backend failures (e.g. SQL
  Server `sp_getapplock` deadlock-victim and validation errors), distinct from
  `LockAcquisitionTimeoutException`.

### Notes

The original v0.2.0 scope (sp_getapplock + Postgres advisory locks + multi-master
RedLock + concurrency stress harness) was split into successive minor releases.
This 0.2.0 ships only the SQL Server backend; Postgres, RedLock, and the stress
harness will follow as 0.2.1+.

## [0.1.1] - 2026-05-23

### Changed

- New minimalist family-style logo (padlock with an Orion-star keyhole, indigo line-art, no badge ring) replaces the v0.1.0 circular emblem. Applied to the README and to every package's NuGet icon.

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
