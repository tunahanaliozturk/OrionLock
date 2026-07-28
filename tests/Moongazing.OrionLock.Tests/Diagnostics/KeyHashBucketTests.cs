namespace Moongazing.OrionLock.Tests.Diagnostics;

using System.Diagnostics.Metrics;
using Moongazing.OrionLock.Diagnostics;
using Xunit;

[CollectionDefinition(nameof(KeyHashBucketTests), DisableParallelization = true)]
#pragma warning disable CA1711
public sealed class KeyHashBucketTestsCollection { }
#pragma warning restore CA1711

[Collection(nameof(KeyHashBucketTests))]
public sealed class KeyHashBucketTests
{
    [Fact]
    public void HashKeyToBucket_returns_stable_value_for_same_key_across_calls()
    {
        var a = OrionLockDiagnostics.HashKeyToBucket("user:42:orders");
        var b = OrionLockDiagnostics.HashKeyToBucket("user:42:orders");
        Assert.Equal(a, b);
    }

    [Fact]
    public void HashKeyToBucket_returns_bucket_in_valid_range_0_to_63()
    {
        for (int i = 0; i < 1_000; i++)
        {
            var bucket = OrionLockDiagnostics.HashKeyToBucket($"key-{i}");
            var asInt = int.Parse(bucket, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(asInt, 0, 63);
        }
    }

    [Fact]
    public void HashKeyToBucket_treats_null_and_empty_as_zero_bucket()
    {
        Assert.Equal("0", OrionLockDiagnostics.HashKeyToBucket(null!));
        Assert.Equal("0", OrionLockDiagnostics.HashKeyToBucket(string.Empty));
    }

    [Fact]
    public void RecordAcquireTimeout_with_key_emits_key_hash_tag()
    {
        var samples = new System.Collections.Generic.List<(string keyHash, long val)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Moongazing.OrionLock"
                && instrument.Name == "orion.lock.acquire.timeout")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, val, tags, _) =>
        {
            string hash = string.Empty;
            foreach (var t in tags)
            {
                if (t.Key == "key_hash" && t.Value is string s) { hash = s; }
            }
            lock (samples) { samples.Add((hash, val)); }
        });
        listener.Start();

        typeof(OrionLockDiagnostics)
            .GetMethod("RecordAcquireTimeout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static, null, new[] { typeof(string) }, null)!
            .Invoke(null, new object[] { "user:42:checkout" });

        lock (samples)
        {
            Assert.Contains(samples, s => !string.IsNullOrEmpty(s.keyHash) && s.val == 1);
        }
    }
}
