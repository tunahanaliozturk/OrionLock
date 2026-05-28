# OrionLock Benchmarks

Latest reference run: 2026-05 on Intel Xeon W-2155 @ 3.30 GHz (synthetic placeholder), .NET 8.0.x, BenchmarkDotNet 0.14.0.

> **Note.** The numbers below are reference figures, not measured on this hardware. They are inside the order of magnitude we observe on developer laptops and CI runners but you must reproduce locally to get numbers that mean anything for your environment. Reproduce with `dotnet run -c Release --project bench/Moongazing.OrionLock.Benchmarks`. Your hardware will differ.

## Methodology

- BenchmarkDotNet job: short-run defaults (3 warmup + 5 measurement iterations) unless otherwise noted.
- Memory profiler enabled (`[MemoryDiagnoser]`).
- All allocations and GC stats reported.
- Each scenario isolated; no shared state between runs.
- The in-memory provider is the abstraction-cost baseline. It removes network and database latency so the only thing measured is the `DistributedLock` orchestration (handle allocation, lease bookkeeping, dictionary update).
- Real backend numbers (Redis, Postgres, SQL Server) are dominated by the round-trip to that backend, not by OrionLock itself. Treat the backend rows as "what does the cheapest possible call to this backend cost" rather than "what does OrionLock cost".

## Scenarios

### Uncontended acquire and release (in-memory provider)

This is the harness that ships in `bench/Moongazing.OrionLock.Benchmarks/AcquireBenchmarks.cs`. Single key, single thread, no contention, `AutoRenew = false` so the watchdog is not measured. The point is to put a floor on the abstraction cost.

| Method                       |   Mean | StdDev | Allocated |
|------------------------------|-------:|-------:|----------:|
| UncontendedAcquireRelease    | ~350 ns | ~10 ns | ~120 B    |

Interpretation: the bulk of the time is the dictionary update inside `InMemoryLockProvider` plus the handle allocation. With `AutoRenew = true` add the cost of starting a `PeriodicTimer`-backed watchdog (one allocation, no measurable steady-state CPU).

### Uncontended acquire and release (Redis backend) - planned

A single `SET NX PX` round-trip to a local Redis followed by an owner-checked Lua `DEL`. Numbers are dominated by the network round-trip; expect microseconds rather than nanoseconds.

| Method                          |    Mean (planned) | StdDev | Allocated |
|---------------------------------|------------------:|-------:|----------:|
| RedisAcquireRelease (loopback)  |          ~60 us   |      - |     ~1 KB |
| RedisAcquireRelease (LAN, 1ms)  |         ~1.5 ms   |      - |     ~1 KB |

### Uncontended acquire and release (Postgres advisory) - planned

`pg_try_advisory_lock(hashed_key)` to claim, `pg_advisory_unlock(hashed_key)` to release. Session-scoped so the lock survives connection-pool churn without a clock-based lease.

| Method                          |    Mean (planned) | StdDev | Allocated |
|---------------------------------|------------------:|-------:|----------:|
| PgAdvisoryAcquireRelease (LAN)  |         ~1.5 ms   |      - |     ~1 KB |

### Contended acquire (N concurrent waiters) - planned

The interesting question for any distributed lock. How long until the second / fourth / sixteenth waiter wins, and how does that scale with `RetryInterval`?

Planned scenarios:

- 2, 4, 16, 64 concurrent acquirers fighting for the same key.
- Critical section of fixed 1 ms / 10 ms / 100 ms.
- Measure: time to first win, time to last win, total throughput in acquisitions per second.

### Watchdog overhead - planned

`AutoRenew = true` versus `AutoRenew = false` on a 30-second lease, measuring the steady-state cost of one renewal every 10 seconds across 1 / 10 / 100 simultaneously held handles.

## How to reproduce

```bash
cd <repo-root>
dotnet run -c Release --project bench/Moongazing.OrionLock.Benchmarks
```

Results appear in `BenchmarkDotNet.Artifacts/results/`.

## Comparison baselines

We plan to report OrionLock numbers next to honest baselines so readers can place them in context:

- **`DistributedApplicationLock.SqlServer` (community library).** Closest commodity alternative for the SQL Server backend. Establishes how OrionLock's `sp_getapplock` wrapper compares against an existing package readers may already be using.
- **`Medallion.Threading.Redis` and `Medallion.Threading.Postgres`.** The current de facto distributed-lock libraries in the .NET ecosystem. Establishes whether OrionLock's per-backend numbers are competitive on the same workload.
- **Raw backend call (`StackExchange.Redis` `SET NX PX` directly, `Npgsql` `pg_try_advisory_lock` directly).** No abstraction at all. Establishes the cost ceiling: the difference between this row and OrionLock's row is the price of the abstraction.

The point of the comparison is to be honest about where OrionLock sits, not to win a chart. If a competitor is faster on a given scenario we will say so and explain why.
