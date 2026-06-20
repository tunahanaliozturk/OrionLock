using System.Reflection;

namespace Moongazing.OrionLock.Diagnostics;

/// <summary>
/// Resolves the instrumentation version for the OrionLock <see cref="System.Diagnostics.ActivitySource"/>
/// and <see cref="System.Diagnostics.Metrics.Meter"/> from the running assembly, so the telemetry version
/// tracks the package version automatically and cannot drift behind a release bump.
/// </summary>
internal static class MeterVersion
{
    /// <summary>
    /// The resolved instrumentation version (e.g. <c>0.4.1</c>), derived from the assembly's
    /// <see cref="AssemblyInformationalVersionAttribute"/> with any build metadata suffix stripped.
    /// </summary>
    public static string Value { get; } = Resolve();

    private static string Resolve()
    {
        var asm = typeof(MeterVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
