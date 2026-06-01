# Lock-key cardinality and OrionLock telemetry

OrionLock's Meter exposes per-acquire and per-renewal histograms tagged with a backend identifier (`redis`, `sqlserver`, `postgres`, `efcore`, `inmemory`). Backend identifiers are a fixed, small set, so those tags are safe to aggregate on. Lock keys are not. Application keys are user input - they routinely encode tenant ids, order ids, customer ids, or worse. Pushing the raw key into a metric tag turns the Meter's time-series store into an unbounded write-amplification engine, and the cost shows up first as ingestion lag in your metrics backend, then as dropped points, and finally as memory pressure on the host process.

For this reason OrionLock never adds the lock `key` as a metric tag. It does attach the key as an `ActivitySource` span tag, which is acceptable because spans are sampled and stored per-trace rather than aggregated into a series. If you wrap OrionLock and add your own metrics, follow the same rule.

**Do** keep keys out of metric tags, and bucket them into a small fixed set of categories if you really need a per-category view.

```csharp
// Good: bucket by stable category before tagging.
var category = key.StartsWith("order:") ? "order" : "other";
myCounter.Add(1, new KeyValuePair<string, object?>("key_category", category));
```

**Don't** put raw keys on a metric.

```csharp
// Bad: each unique order id becomes its own time series.
myCounter.Add(1, new KeyValuePair<string, object?>("key", $"order:{orderId}"));
```

For traces, the raw key on the span tag is fine because per-span storage scales differently than per-series storage. For metrics, treat the lock key as PII-shaped: do not aggregate on it.
