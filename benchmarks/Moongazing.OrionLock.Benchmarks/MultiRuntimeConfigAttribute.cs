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
        : this(singleInvocationPerIteration: false)
    {
    }

    /// <param name="singleInvocationPerIteration">
    /// When true, each iteration runs the benchmark exactly once. Required by any class that uses
    /// <c>[IterationSetup]</c> to rebuild per-operation state (such as re-acquiring the handle a
    /// release benchmark is about to dispose): with the default unrolled invocation count the setup
    /// would run once for many invocations and every invocation after the first would measure a
    /// no-op. The trade-off is a coarser timer resolution, which is why it is opt-in.
    /// </param>
    public MultiRuntimeConfigAttribute(bool singleInvocationPerIteration)
    {
        var config = ManualConfig.CreateEmpty().AddDiagnoser(MemoryDiagnoser.Default);
        Job[] jobs =
        [
            Job.Default.WithRuntime(CoreRuntime.Core80).WithId("net8.0"),
            Job.Default.WithRuntime(CoreRuntime.Core90).WithId("net9.0"),
            Net10Job.Create(),
        ];
        foreach (var job in jobs)
        {
            config = config.AddJob(singleInvocationPerIteration
                ? job.WithInvocationCount(1).WithUnrollFactor(1)
                : job);
        }
        Config = config;
    }

    public IConfig Config { get; }
}
