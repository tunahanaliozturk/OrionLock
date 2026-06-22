using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Moongazing.OrionLock.SqlServer.Tests;

/// <summary>
/// One real SQL Server container (Testcontainers) shared by every test in a fixture-consuming class.
/// </summary>
/// <remarks>
/// CI resilience: SQL Server is by far the slowest of the suite's containers to become connectable,
/// and the matrix runs three TFM jobs on one runner with SqlServer + Postgres + Redis + Etcd +
/// ZooKeeper + Consul all starting together. Under that load the first client connection after the
/// container's <c>StartAsync</c> can blip (login not yet accepting, sluggish first handshake) even
/// after the module's in-container <c>SELECT 1</c> probe reports ready, and the Docker daemon can
/// transiently fault the start. To stop either from failing the WHOLE test class at once, the container
/// start and a real <c>SELECT 1</c> warm-up round-trip are retried with exponential backoff under a
/// generous budget (the largest in the suite, since SQL Server needs it most).
/// </remarks>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder().Build();

    public string ConnectionString { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await ContainerStartup.StartWithWarmupAsync(
            container,
            // SQL Server is the slowest to accept logins on a loaded runner; give it the most headroom.
            budget: TimeSpan.FromMinutes(5),
            warmupAsync: async ct =>
            {
                await using var connection = new SqlConnection(container.GetConnectionString());
                await connection.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1";
                _ = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        ConnectionString = container.GetConnectionString();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

/// <summary>
/// Shared, provider-agnostic helper that starts a Testcontainers container and runs a real
/// application-protocol warm-up, retrying both with exponential backoff under an overall time budget.
/// </summary>
/// <remarks>
/// This is the single biggest lever against the flaky CI suite: a slow first connection on a loaded
/// runner is retried rather than failing an entire provider test class. The container's own module
/// readiness probe is left in place (it checks the real protocol inside the container); this only adds
/// tolerance for transient faults during start and the first out-of-container connection.
/// </remarks>
internal static class ContainerStartup
{
    /// <summary>
    /// Starts <paramref name="container"/> and runs <paramref name="warmupAsync"/> (a real protocol
    /// round-trip that closes over the strongly-typed container), retrying the whole sequence on
    /// transient failure with exponential backoff until it succeeds or <paramref name="budget"/> is
    /// exhausted, after which the last error is rethrown.
    /// </summary>
    public static async Task StartWithWarmupAsync(
        DotNet.Testcontainers.Containers.IContainer container,
        TimeSpan budget,
        Func<CancellationToken, Task> warmupAsync)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(warmupAsync);

        var sw = Stopwatch.StartNew();
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(10);
        var attempt = 0;
        Exception? last = null;

        while (sw.Elapsed < budget)
        {
            attempt++;
            using var cts = new CancellationTokenSource(budget - sw.Elapsed);
            try
            {
                // StartAsync is idempotent once started, so retrying after a warm-up failure is safe and
                // cheap (it returns immediately when the container is already running).
                await container.StartAsync(cts.Token).ConfigureAwait(false);
                await warmupAsync(cts.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                last = ex;
                var remaining = budget - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var wait = delay < remaining ? delay : remaining;
                await Task.Delay(wait).ConfigureAwait(false);
                delay = delay + delay < maxDelay ? delay + delay : maxDelay;
            }
        }

        throw new InvalidOperationException(
            $"Container '{container.Name}' did not become ready within {budget.TotalSeconds:N0}s after {attempt} attempt(s).",
            last);
    }
}
