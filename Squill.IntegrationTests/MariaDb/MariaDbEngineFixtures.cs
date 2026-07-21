using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Testcontainers.MariaDb;
using Testcontainers.MySql;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// A running database container for one MariaDB-family engine, shared across all the tests
/// of a test class (xUnit class fixture). The same provider is exercised against both a
/// MariaDB and a MySQL server (issue #12), so each scenario runs once per engine via the
/// two concrete fixtures below.
/// </summary>
public abstract class MariaDbLikeFixture : IAsyncLifetime
{
    private IDatabaseContainer? _container;

    /// <summary>The friendly engine name, used in test display names.</summary>
    public abstract string EngineName { get; }

    /// <summary>Builds (but does not start) the engine's container.</summary>
    protected abstract IDatabaseContainer BuildContainer();

    /// <summary>A connection string to the running server (root credentials).</summary>
    public string ConnectionString => (_container ?? throw new InvalidOperationException(
        "The container has not been started.")).GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        _container = BuildContainer();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>A MariaDB engine fixture (mariadb:latest).</summary>
public sealed class MariaDbFixture : MariaDbLikeFixture
{
    public override string EngineName => "MariaDB";

    // Squill's deploy path creates and drops databases and reads information_schema, so the
    // test connection must be the root account — the default per-database user the container
    // provisions cannot CREATE DATABASE. WithUsername("root") makes GetConnectionString()
    // return a root connection string.
    protected override IDatabaseContainer BuildContainer() =>
        new MariaDbBuilder(new DockerImage("mariadb:latest"))
            .WithUsername("root")
            .Build();
}

/// <summary>A MySQL engine fixture (mysql:latest), proving the same provider works on MySQL.</summary>
public sealed class MySqlFixture : MariaDbLikeFixture
{
    public override string EngineName => "MySQL";

    protected override IDatabaseContainer BuildContainer() =>
        new MySqlBuilder(new DockerImage("mysql:latest"))
            .WithUsername("root")
            .Build();
}
