using System.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Moongazing.OrionLock.Postgres.Tests;

/// <summary>
/// One real PostgreSQL container (Testcontainers) shared by every test in a fixture-consuming class.
/// </summary>
/// <remarks>
/// CI resilience: the build-and-test matrix runs three TFM jobs on one runner, each spinning up
/// SqlServer + Postgres + Redis + Etcd + ZooKeeper + Consul containers at the same time. Under that
/// load the FIRST client connection after the container's <c>StartAsync</c> can blip even though the
/// module's in-container <c>pg_isready</c> probe has already reported the server ready, and
/// the Docker daemon itself can transiently fault the start. To stop either from failing the WHOLE
/// test class at once, the container start and a real <c>SELECT 1</c> warm-up round-trip are retried a
/// bounded number of times with exponential backoff under a generous overall budget. The module's own
/// readiness probe (which runs the real protocol check inside the container) is kept as the primary
/// gate; this wrapper only adds tolerance for transient warm-up faults on a loaded runner.
/// </remarks>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder().Build();

    public string ConnectionString { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await ContainerStartup.StartWithWarmupAsync(
            container,
            // Generous budget for a heavily loaded multi-container runner; Postgres warms up fast once
            // the daemon is responsive, so two minutes is ample headroom over the real need.
            budget: TimeSpan.FromMinutes(2),
            warmupAsync: async ct =>
            {
                await using var connection = new NpgsqlConnection(container.GetConnectionString());
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = connection.CreateCommand();
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
