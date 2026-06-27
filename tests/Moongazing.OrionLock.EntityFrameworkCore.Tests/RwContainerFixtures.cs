using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Moongazing.OrionLock.EntityFrameworkCore.Tests;

/// <summary>
/// Base for a Testcontainers-backed fixture that stands up a real relational database, builds a service
/// provider with <see cref="RwLockDbContext"/> registered against it, and exposes an
/// <see cref="IServiceScopeFactory"/> the EF Core reader-writer provider resolves a scoped context from
/// (exactly as production does). The schema is created once via <c>EnsureCreatedAsync</c>.
/// </summary>
public abstract class RwContainerFixtureBase : IAsyncLifetime
{
    private ServiceProvider services = default!;

    /// <summary>The scope factory the provider-under-test resolves <see cref="DbContext"/> from.</summary>
    public IServiceScopeFactory ScopeFactory { get; private set; } = default!;

    /// <summary>Starts the container (with the hardened retry-warmup), wires DI, and creates the schema.</summary>
    public async Task InitializeAsync()
    {
        var connectionString = await StartAndGetConnectionStringAsync().ConfigureAwait(false);

        var sc = new ServiceCollection();
        sc.AddDbContext<RwLockDbContext>(ConfigureDbContext(connectionString), ServiceLifetime.Scoped);
        sc.AddScoped<DbContext>(sp => sp.GetRequiredService<RwLockDbContext>());
        services = sc.BuildServiceProvider();
        ScopeFactory = services.GetRequiredService<IServiceScopeFactory>();

        await using var scope = services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<RwLockDbContext>();
        await ctx.Database.EnsureCreatedAsync().ConfigureAwait(false);
    }

    /// <summary>Disposes the service provider and the container.</summary>
    public async Task DisposeAsync()
    {
        await services.DisposeAsync().ConfigureAwait(false);
        await DisposeContainerAsync().ConfigureAwait(false);
    }

    /// <summary>Starts the container with the hardened retry-warmup and returns its connection string.</summary>
    protected abstract Task<string> StartAndGetConnectionStringAsync();

    /// <summary>Configures the EF Core provider (Npgsql / SqlServer) for the connection string.</summary>
    protected abstract Action<DbContextOptionsBuilder> ConfigureDbContext(string connectionString);

    /// <summary>Disposes the underlying container.</summary>
    protected abstract Task DisposeContainerAsync();
}

/// <summary>One real PostgreSQL container shared by the EF Core reader-writer conformance tests.</summary>
public sealed class PostgresRwContainerFixture : RwContainerFixtureBase
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder().Build();

    protected override async Task<string> StartAndGetConnectionStringAsync()
    {
        await ContainerStartup.StartWithWarmupAsync(
            container,
            budget: TimeSpan.FromMinutes(2),
            warmupAsync: async ct =>
            {
                await using var scope = new ServiceCollection()
                    .AddDbContext<RwLockDbContext>(o => o.UseNpgsql(container.GetConnectionString()))
                    .BuildServiceProvider()
                    .CreateAsyncScope();
                var ctx = scope.ServiceProvider.GetRequiredService<RwLockDbContext>();
                _ = await ctx.Database.CanConnectAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return container.GetConnectionString();
    }

    protected override Action<DbContextOptionsBuilder> ConfigureDbContext(string connectionString)
        => o => o.UseNpgsql(connectionString);

    protected override Task DisposeContainerAsync() => container.DisposeAsync().AsTask();
}

/// <summary>One real SQL Server container shared by the EF Core reader-writer conformance tests.</summary>
public sealed class SqlServerRwContainerFixture : RwContainerFixtureBase
{
    private readonly MsSqlContainer container = new MsSqlBuilder().Build();

    protected override async Task<string> StartAndGetConnectionStringAsync()
    {
        await ContainerStartup.StartWithWarmupAsync(
            container,
            // SQL Server is the slowest to accept logins on a loaded runner; give it the most headroom.
            budget: TimeSpan.FromMinutes(5),
            warmupAsync: async ct =>
            {
                await using var scope = new ServiceCollection()
                    .AddDbContext<RwLockDbContext>(o => o.UseSqlServer(container.GetConnectionString()))
                    .BuildServiceProvider()
                    .CreateAsyncScope();
                var ctx = scope.ServiceProvider.GetRequiredService<RwLockDbContext>();
                _ = await ctx.Database.CanConnectAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return container.GetConnectionString();
    }

    protected override Action<DbContextOptionsBuilder> ConfigureDbContext(string connectionString)
        => o => o.UseSqlServer(connectionString);

    protected override Task DisposeContainerAsync() => container.DisposeAsync().AsTask();
}

/// <summary>
/// Shared, provider-agnostic helper that starts a Testcontainers container and runs a real
/// application-protocol warm-up, retrying both with exponential backoff under an overall time budget. The
/// same pattern the Postgres / SqlServer test fixtures use: it is the single biggest lever against the
/// flaky CI suite, retrying a slow first connection on a loaded runner rather than failing the class.
/// </summary>
internal static class ContainerStartup
{
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
