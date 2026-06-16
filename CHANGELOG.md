# Changelog

All notable changes to OrionLock are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
