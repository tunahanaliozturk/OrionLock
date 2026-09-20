using System.Runtime.InteropServices;

namespace Moongazing.OrionLock.Tests.Containers;

/// <summary>
/// A <see cref="FactAttribute"/> for a test that needs a container. Without a reachable Docker daemon
/// the test is skipped instead of failing: a machine with no Docker cannot say anything about the code,
/// and 135 red tests hide the ones that mean something.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    /// <inheritdoc cref="DockerFactAttribute"/>
    public DockerFactAttribute()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            Skip = DockerEnvironment.SkipReason;
        }
    }
}

/// <summary>Theory counterpart of <see cref="DockerFactAttribute"/>.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    /// <inheritdoc cref="DockerTheoryAttribute"/>
    public DockerTheoryAttribute()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            Skip = DockerEnvironment.SkipReason;
        }
    }
}

/// <summary>
/// The images the container-backed suites run against. Pinned in one place: Testcontainers 4 requires an
/// explicit image, and four suites picking their own tags would drift apart one copy-paste at a time.
/// </summary>
public static class ContainerImages
{
    /// <summary>Redis image used by the Redis suites.</summary>
    public const string Redis = "redis:7.4";

    /// <summary>PostgreSQL image used by the Postgres and EF Core suites.</summary>
    public const string PostgreSql = "postgres:16-alpine";

    /// <summary>SQL Server image used by the SQL Server and EF Core suites.</summary>
    public const string SqlServer = "mcr.microsoft.com/mssql/server:2022-latest";
}

/// <summary>Whether this machine can start containers, decided once per test run.</summary>
public static class DockerEnvironment
{
    /// <summary>Shown on every skipped test, so a green run still says why it was cheap.</summary>
    public const string SkipReason = "Docker is not reachable on this machine, so the container-backed tests cannot run.";

    private static readonly Lazy<bool> Available = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when a Docker endpoint answers. Probed once; the result is cached for the run.</summary>
    public static bool IsAvailable => Available.Value;

    // The endpoint is probed by existence, not by an API call: opening the pipe or socket is enough to
    // tell a machine without Docker from one with it, and it costs no daemon round trip. DOCKER_HOST wins
    // when it is set, because that is what Testcontainers itself honours first.
    private static bool Probe()
    {
        var configured = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Directory.Exists(@"\\.\pipe")
                && Directory.EnumerateFiles(@"\\.\pipe").Any(static pipe =>
                    pipe.Contains("docker_engine", StringComparison.OrdinalIgnoreCase)
                    || pipe.Contains("dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase));
        }

        return File.Exists("/var/run/docker.sock");
    }
}
