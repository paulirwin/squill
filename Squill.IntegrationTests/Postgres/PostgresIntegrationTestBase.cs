using DotNet.Testcontainers.Images;
using Testcontainers.PostgreSql;

namespace Squill.IntegrationTests.Postgres;

public abstract class PostgresIntegrationTestBase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresqlContainer = new PostgreSqlBuilder(new DockerImage("postgres:latest"))
        .Build();

    protected string ConnectionString => _postgresqlContainer.GetConnectionString();

    public async ValueTask InitializeAsync() => await _postgresqlContainer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _postgresqlContainer.DisposeAsync();
    }
}
