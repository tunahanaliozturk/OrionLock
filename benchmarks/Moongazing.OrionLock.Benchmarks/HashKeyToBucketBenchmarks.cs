using System.Globalization;
using BenchmarkDotNet.Attributes;
using Moongazing.OrionLock.Diagnostics;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// Measures <see cref="OrionLockDiagnostics.HashKeyToBucket(string)"/>, the FNV-1a hash that maps a
/// lock key onto one of 64 cardinality-bounded metric buckets. It runs on the acquire-timeout path
/// for every distinct key, so it must stay allocation-light and CPU-cheap even under a multi-tenant
/// key space of millions of unique strings. The baseline is a naive <c>string.GetHashCode</c>-based
/// bucketer that shows what a per-AppDomain-randomized hash would cost (and why it is unsuitable:
/// it is not stable across processes), giving a like-for-like comparison against the real FNV-1a.
/// </summary>
[MultiRuntimeConfig]
public class HashKeyToBucketBenchmarks
{
    private const int BucketCount = 64;

    [Params("order", "tenant-42:resource:inventory:sku-0099812", "")]
    public string Key { get; set; } = string.Empty;

    /// <summary>The real production FNV-1a bucketer over UTF-16 chars.</summary>
    [Benchmark]
    public string HashKeyToBucket() => OrionLockDiagnostics.HashKeyToBucket(Key);

    /// <summary>
    /// Naive baseline using the framework's randomized string hash. Faster to type but unfit for
    /// purpose: it is randomized per process so the same key lands in different buckets across hosts.
    /// Included only to anchor the FNV-1a cost against a familiar reference.
    /// </summary>
    [Benchmark(Baseline = true)]
    public string NaiveStringHashBucket()
    {
        if (string.IsNullOrEmpty(Key))
        {
            return "0";
        }
        var bucket = (Key.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF) % BucketCount;
        return bucket.ToString(CultureInfo.InvariantCulture);
    }
}
