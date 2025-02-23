using Testcontainers.PostgreSql;

namespace Squill.IntegrationTests.Postgres;

public abstract class PostgresIntegrationTestBase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresqlContainer = new PostgreSqlBuilder()
        .Build();

    public string ConnectionString => _postgresqlContainer.GetConnectionString();

    public Task InitializeAsync() => _postgresqlContainer.StartAsync();

    public Task DisposeAsync() => _postgresqlContainer.DisposeAsync().AsTask();
}
