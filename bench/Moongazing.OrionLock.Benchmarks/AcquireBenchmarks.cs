using BenchmarkDotNet.Attributes;
using Moongazing.OrionLock;
using Moongazing.OrionLock.Testing;

namespace Moongazing.OrionLock.Benchmarks;

[MemoryDiagnoser]
public class AcquireBenchmarks
{
    private DistributedLock locker = default!;

    [GlobalSetup]
    public void Setup() => locker = new DistributedLock(new InMemoryLockProvider());

    [Benchmark]
    public async Task UncontendedAcquireRelease()
    {
        await using var h = await locker.AcquireAsync("bench-key",
            new DistributedLockOptions { AutoRenew = false });
    }
}
