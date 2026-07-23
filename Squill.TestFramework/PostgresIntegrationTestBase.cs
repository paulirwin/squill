using DotNet.Testcontainers.Images;
using Testcontainers.PostgreSql;
using Xunit;

namespace Squill.TestFramework;

/// <summary>
/// Base class for a PostgreSQL integration test: a lazily-created, per-class Testcontainers
/// Postgres container started once for the test class and disposed at the end.
/// </summary>
public abstract class PostgresIntegrationTestBase : IAsyncLifetime
{
    // The Docker image used for the test container. Defaults to the stock postgres
    // image; tests that need contrib types/extensions (e.g. pgvector) override this.
    protected virtual string DockerImageName => "postgres:latest";

    private PostgreSqlContainer? _postgresqlContainer;

    private PostgreSqlContainer Container =>
        _postgresqlContainer ??= new PostgreSqlBuilder(new DockerImage(DockerImageName)).Build();

    protected string ConnectionString => Container.GetConnectionString();

    public async ValueTask InitializeAsync() => await Container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_postgresqlContainer is not null)
        {
            await _postgresqlContainer.DisposeAsync();
        }
    }
}
