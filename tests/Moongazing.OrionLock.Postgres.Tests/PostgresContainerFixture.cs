using Testcontainers.PostgreSql;

namespace Moongazing.OrionLock.Postgres.Tests;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder().Build();

    public string ConnectionString { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await container.StartAsync().ConfigureAwait(false);
        ConnectionString = container.GetConnectionString();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
