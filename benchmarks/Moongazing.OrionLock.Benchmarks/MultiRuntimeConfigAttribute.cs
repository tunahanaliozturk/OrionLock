using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// Applies the suite-wide benchmark configuration to a class: a memory diagnoser plus one job per
/// supported runtime (.NET 8, .NET 9, .NET 10). The .NET 8 and .NET 9 jobs use BenchmarkDotNet's
/// built-in <see cref="RuntimeMoniker"/> values; the .NET 10 job is hand-built (see
/// <see cref="Net10Job"/>) because BenchmarkDotNet 0.14.0 has no .NET 10 moniker. Centralizing this
/// keeps every benchmark class running on the same three runtimes without repeating the job wiring.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class MultiRuntimeConfigAttribute : Attribute, IConfigSource
{
    public MultiRuntimeConfigAttribute()
    {
        Config = ManualConfig.CreateEmpty()
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddJob(Job.Default.WithRuntime(CoreRuntime.Core80).WithId("net8.0"))
            .AddJob(Job.Default.WithRuntime(CoreRuntime.Core90).WithId("net9.0"))
            .AddJob(Net10Job.Create());
    }

    public IConfig Config { get; }
}
