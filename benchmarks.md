# OrionLock Benchmarks

A BenchmarkDotNet suite that measures the dependency-free hot paths of OrionLock: the core lock
orchestration under contention and at rest, lease renewal at scale, release on its own, the
reader-writer path, the FIFO fairness coordinator, the metric key-bucketing hash, and same-process
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

Measures `InProcessFifoWaiterCoordinator` across a range of queue depths (0, 8, 64, 256). One caller
takes the head of the per-key FIFO queue, `QueueDepth` more line up behind it, then the whole queue
drains in arrival order.

Depth 0 is the uncontended round trip this class used to measure on its own: enter an empty queue,
become the head immediately, leave. That is the overhead the opt-in fair-lock option adds to every
blocking `AcquireAsync` under low contention, and it remains the baseline row.

The larger depths exist because the empty queue structurally cannot show what the coordinator does
per enter: it counts the live waiters ahead of the newcomer with a LINQ scan over the whole queue,
under the coordinator's single process-wide lock. That scan is O(N) in the current queue depth, so
building a queue of N does O(N^2) scanning in total, and because the lock is global rather than per
key, every key in the process serializes behind it.

Read the mean as a batch, not as a per-waiter cost. One invocation performs all `QueueDepth + 1`
enter/leave pairs, and `OperationsPerInvoke` cannot divide it down because that attribute takes a
compile-time constant and `QueueDepth` is a `[Params]` value. The reported mean and allocations are
therefore the cost of building and draining the whole queue. Dividing by `QueueDepth + 1` is a
derivation you have to make deliberately; it is not what the table says.

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

## Benchmarks added as a pre-performance-work baseline

Everything in this section was added to make the performance work measurable BEFORE anyone changes
it. The numbers these produce today are a baseline, not a target and not an endorsement: they record
what the current implementation costs, so a change can be shown to have improved or regressed it.
Until this went in, the suite measured only the uncontended, single-handle, watchdog-off case, so
none of the costs below were visible at all.

### ContentionBenchmarks

Races 2, 8, 64 and 256 concurrent blocking `AcquireAsync` callers for a SINGLE key, and reports both
the drain latency and the number of `TryAcquireAsync` calls the backend actually saw.

The call count is the point. `AcquireAsync` polls at a flat `RetryInterval` with no jitter and no
backoff, so every waiter wakes on the same tick and the store sees an N-wide burst each interval,
while only one of them can win. Against the in-process provider each call costs nanoseconds, so the
latency column alone would never reveal this; the reported per-operation call count does, and it
grows faster than the waiter count.

Two details are deliberate. Each waiter gets its own `DistributedLock` instance over a shared
provider, because reentrancy is tracked per instance and N waiters sharing one instance would
collapse into nested handles and never contend at all. And a gate handle holds the key until every
waiter has, for itself, seen one refused attempt, so the burst is deterministic instead of a
function of how fast the thread pool ramps.

That barrier is per waiter, not an aggregate count of refusals, and the difference matters. An
aggregate cannot tell N waiters that each failed once apart from one fast waiter that failed N
times, so at the larger waiter counts it would open the gate while other callers were still sitting
in the thread-pool queue; those callers would find the key free, and the measurement would be of
thread-pool scheduling rather than contention. The barrier costs an exact, known number of provider
calls per operation, one gate acquire plus one probe per waiter, and the report prints the call
totals both raw and net of it. Compare the net figure.

The retry interval is scaled down from the 250 ms default purely to keep wall time tractable; the
call count is interval-independent, because each poll round still hands the key to exactly one
waiter.

### RenewalScaleBenchmarks

Holds 1, 100 and 1000 auto-renewing handles for a fixed window, and reports allocations, renewals
issued and wall time. This is the only class in the suite that runs with `AutoRenew = true`.

Every held handle owns a private task, a private `CancellationTokenSource` and a private
`Task.Delay` timer, and renews at exactly `LeaseDuration / 3` with no jitter. A host holding N leases
therefore carries N background tasks and N timers, and because the interval is a fixed fraction of
the lease with no spread, handles acquired together keep renewing together: the store sees N
renewals on one tick rather than N spread over the interval. The allocation column is the per-handle
overhead; the printed renewal count is the traffic.

Renewals are counted across the hold window only, between a snapshot taken once every handle exists
and one taken before the first disposal. A watchdog starts the moment its handle is constructed, so
a handle acquired early is already renewing while later ones are still being created, and it keeps
renewing while earlier ones are being disposed. Counting the whole method would fold an N-dependent
acquisition and disposal window into the figure and make the per-handle number at 1000 incomparable
with the one at 1. The window is the same 200 ms for every N, so the per-handle figures can be read
side by side. The reported `Mean` is still the whole method, acquire plus window plus dispose, which
is why it rises with N while the per-handle renewal count stays roughly flat.

### ReleaseBenchmarks

Measures disposal on its own, with `AutoRenew` on and off. Everywhere else the release path is fused
to an acquire inside a single measured method, so its cost has never been separable, and the two
halves are not symmetric: acquire is one provider call, while disposing an auto-renewing handle must
cancel the watchdog's token source and await the watchdog task before it can issue the release.

The on/off pair is the reading. The gap between the two rows is what every auto-renewing handle
costs to put down, paid inline on the caller's way out of the `using` block. The handle is acquired
in `[IterationSetup]` so only the dispose is timed, which is why this class runs one invocation per
iteration; that buys a coarser timer, so read the rows as the on/off delta rather than as an
absolute figure.

### SharedExclusiveLockBenchmarks

Covers `SharedExclusiveLock`, the reader-writer path, which had no benchmark at all. It is a
separate orchestration from `DistributedLock` (its own retry loop, no reentrancy registry, no FIFO
coordinator), so none of the exclusive-lock numbers transfer to it.

Three shapes are measured: the uncontended shared acquire, which is the floor a reader pays; the
shared-on-shared acquire, which is what a second concurrent reader costs while a first one holds and
should land within noise of the baseline, since making that case cheap is the whole reason to reach
for a reader-writer lock; and a writer waiting on readers, where the writer is refused while a
reader holds, records the backend's pending-writer reservation, polls, and takes the key once the
reader drains. The handoff is deterministic: the writer's first attempt runs inline before
`AcquireExclusiveAsync` returns its task, so it is guaranteed to have been refused before the reader
releases. What is left to measure is the poll-driven wake-up, because nothing notifies the writer.

Expect the handoff row to land near one operating-system timer tick (about 15.6 ms on Windows)
rather than near the 1 ms retry interval the benchmark configures, because that is what a
`Task.Delay` shorter than a tick actually sleeps for. That is the finding rather than an artefact:
shrinking `RetryInterval` below a tick buys nothing, so handoff latency cannot be tuned down. Only
replacing the poll with a notification can move it.

The backend is the shipped `InMemorySharedExclusiveLockProvider` rather than a bench-local stand-in,
because the writer-fairness behaviour under test lives in the provider and a simplified fake would
measure a different algorithm.

### Backend round trips, asserted in the test suite

The cost of a failed acquire attempt against a real backend is not a timing at all, it is a count of
client calls, and a count belongs in a test rather than in a results table: a number in a table is
something a human has to notice, a failing test is not. `ZooKeeperRoundTripCountTests`,
`ConsulRoundTripCountTests` and `EtcdRoundTripCountTests` use hand-written counting fakes over
`IZooKeeperClientAdapter`, `IConsulClientAdapter` and `IEtcdClientAdapter` to pin exactly what one
attempt costs today:

| Backend   | Failed attempt | Won attempt | The calls                                            |
| --------- | -------------: | ----------: | ---------------------------------------------------- |
| ZooKeeper |              4 |           3 | ensure path, create sequential, get children, delete |
| Consul    |              3 |           2 | create session, KV acquire, destroy session          |
| etcd      |              3 |           2 | lease grant, put-if-absent, lease revoke             |

The failed-attempt column is the one that matters, because every poll of a contended key pays it and
it multiplies by the waiter count and the poll rate. Each backend spends two thirds of a losing
attempt creating and then tearing down a session or lease it never got to use. Like everything else
in this section these are a baseline: when the backend work reduces a count, the expected value in
the test changes in the same commit, with a reason.

## Running

```bash
dotnet run -c Release --project benchmarks/Moongazing.OrionLock.Benchmarks
```

Pass a filter to run a single class, for example:

```bash
dotnet run -c Release --project benchmarks/Moongazing.OrionLock.Benchmarks -- --filter "*HashKeyToBucket*"
```

Results are written to `BenchmarkDotNet.Artifacts/results/`.
