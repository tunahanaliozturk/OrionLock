# OrionLock Benchmarks

A BenchmarkDotNet suite that measures the dependency-free hot paths of OrionLock: the core lock
orchestration, the FIFO fairness coordinator, the metric key-bucketing hash, and same-process
reentrancy. Every scenario runs entirely in-process. None of them touch Redis, SQL Server, Postgres,
ZooKeeper, etcd, or any other external service, so the numbers reflect the cost of the OrionLock
abstraction itself rather than a network or database round-trip.

The project lives in `benchmarks/Moongazing.OrionLock.Benchmarks` and references only the core
`Moongazing.OrionLock` library. A tiny in-process `IDistributedLockProvider`
(`BenchInMemoryLockProvider`) stands in for a real backend so the only thing measured is the
orchestration around the provider call.

## Methodology

- Each benchmark class targets three runtimes via `[SimpleJob]`: .NET 8, .NET 9, and .NET 10. This
  lets you compare the same code path across runtime versions on your own hardware.
- `[MemoryDiagnoser]` is enabled on every class so allocations and GC stats are reported alongside
  timing.
- Scenarios are isolated. The lock-orchestration benchmarks run with `AutoRenew = false` so the
  background renewal watchdog never starts and does not pollute the steady-state acquire cost.
- No measured numbers are published here. Run the suite locally to get figures that mean anything for
  your environment, because results depend heavily on CPU, runtime, and OS.

## Benchmark classes

### HashKeyToBucketBenchmarks

Measures `OrionLockDiagnostics.HashKeyToBucket(string)`, the FNV-1a hash that maps a lock key onto
one of 64 cardinality-bounded metric buckets. This runs on the acquire-timeout path for every
distinct key, so under a multi-tenant key space of millions of unique strings it must stay
allocation-light and CPU-cheap. The class parameterizes over a short key, a long realistic key, and
the empty string. It includes a `Baseline` naive bucketer built on the framework's randomized
`string.GetHashCode` to anchor the FNV-1a cost against a familiar reference and to make concrete why
the randomized hash is unfit for purpose (it is not stable across processes, so the same key would
land in different buckets on different hosts).

### FifoCoordinatorBenchmarks

Measures the uncontended fast path of `InProcessFifoWaiterCoordinator`. A single caller enters the
per-key FIFO queue, becomes the head immediately with no wait, then leaves. This is the overhead the
opt-in fair-lock option adds to every blocking `AcquireAsync` when the queue is empty, which is the
common case under low contention. It exercises the real Enter/Leave contract end to end (queue
allocation, head detection, queue-depth metric emission, ticket disposal), so the figure is the floor
that opting into FIFO ordering imposes before any actual contention exists.

### DistributedLockAcquireBenchmarks

Measures the end-to-end uncontended acquire-and-release cost of the real `DistributedLock` over the
in-process provider. With the backend reduced to a single concurrent-dictionary operation and the
watchdog disabled, what remains is the orchestration cost: owner-token generation, reentrancy
registration, handle allocation, the held-concurrent gauge, and disposal. Two methods are measured:
the non-blocking `TryAcquireAsync` happy path, and the blocking `AcquireAsync` happy path, which
succeeds on the first attempt but still pays for the Activity span, the acquire-duration and
attempt-count metrics, and the FIFO no-op that the non-blocking path skips. Together they isolate
the abstraction floor a caller pays on top of whatever the chosen backend round-trip costs in
production.

### ReentrancyBenchmarks

Measures the same-process reentrancy fast path of `DistributedLock`. The setup holds an outer lease
for the whole run, so every measured acquire is a nested re-entry on an already-held key that must
collapse into a counted nested handle in the reentrancy registry rather than issuing a second backend
call. It quantifies how cheap a recursive critical section is, which matters for code that re-enters a
held lock deep in a call chain (the very scenario the reentrancy-depth metrics exist to surface).

## Running

```bash
dotnet run -c Release --project benchmarks/Moongazing.OrionLock.Benchmarks
```

Pass a filter to run a single class, for example:

```bash
dotnet run -c Release --project benchmarks/Moongazing.OrionLock.Benchmarks -- --filter "*HashKeyToBucket*"
```

Results are written to `BenchmarkDotNet.Artifacts/results/`.
