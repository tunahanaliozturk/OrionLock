using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;

namespace Moongazing.OrionLock.Benchmarks;

/// <summary>
/// BenchmarkDotNet 0.14.0 predates the .NET 10 release, so its <see cref="RuntimeMoniker"/> enum
/// stops at <c>Net90</c> and has no <c>Net10_0</c> member. To still run every benchmark on .NET 10
/// alongside .NET 8 and .NET 9, this exposes a hand-built job whose toolchain targets the
/// <c>net10.0</c> framework moniker directly. Each benchmark class adds it with
/// <c>[SimpleJob]</c>-style coverage via the <see cref="Net10JobAttribute"/>.
/// </summary>
internal static class Net10Job
{
    internal static Job Create() =>
        Job.Default
            .WithToolchain(CsProjCoreToolchain.From(new NetCoreAppSettings(
                targetFrameworkMoniker: "net10.0",
                runtimeFrameworkVersion: null,
                name: ".NET 10.0")))
            .WithId("net10.0");
}
