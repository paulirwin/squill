using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using MySqlConnector;
using Testcontainers.MariaDb;
using Testcontainers.MySql;
using Xunit;

namespace Squill.TestFramework;

/// <summary>
/// A running database container for one MariaDB-family engine, shared across all the tests
/// of a test class (xUnit class fixture). The same provider is exercised against both a
/// MariaDB and a MySQL server (issue #12), so each scenario runs once per engine via the
/// concrete fixtures below.
/// </summary>
public abstract class MariaDbLikeFixture : IAsyncLifetime
{
    private IDatabaseContainer? _container;

    /// <summary>The friendly engine name, used in test display names.</summary>
    public abstract string EngineName { get; }

    /// <summary>
    /// The provider name recorded in a DACPAC targeting this engine (<c>MariaDb</c> or
    /// <c>MySql</c>), which selects the engine's schema-provider types.
    /// </summary>
    public abstract string ProviderName { get; }

    /// <summary>
    /// The lowest supported major version for this engine (see its schema-provider types); any
    /// current test container satisfies it, so a DACPAC targeting it deploys normally.
    /// </summary>
    public abstract int LowestSupportedMajor { get; }

    /// <summary>Builds (but does not start) the engine's container.</summary>
    protected abstract IDatabaseContainer BuildContainer();

    /// <summary>
    /// Which engine this fixture's container must actually be running, checked once at startup
    /// (issue #145). MariaDB and MySQL are distinguished here because a reused host port between
    /// the two would otherwise surface as a confusing dialect difference rather than a collision.
    /// </summary>
    protected abstract ContainerEngine Engine { get; }

    /// <summary>A connection string to the running server (root credentials).</summary>
    public string ConnectionString => (_container ?? throw new InvalidOperationException(
        "The container has not been started.")).GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        _container = BuildContainer();
        await _container.StartAsync();

        await ContainerIdentity.VerifyAsync(() => new MySqlConnection(ConnectionString), Engine);
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

    public override string ProviderName => "MariaDb";

    // Oldest supported MariaDB major (see MariaDb*DatabaseSchemaProvider).
    public override int LowestSupportedMajor => 10;

    protected override ContainerEngine Engine => ContainerEngine.MariaDb;

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

    public override string ProviderName => "MySql";

    // Oldest supported MySQL major (see MySql*DatabaseSchemaProvider).
    public override int LowestSupportedMajor => 8;

    protected override ContainerEngine Engine => ContainerEngine.MySql;

    protected override IDatabaseContainer BuildContainer() =>
        new MySqlBuilder(new DockerImage("mysql:latest"))
            .WithUsername("root")
            .Build();
}

/// <summary>A MariaDB fixture pinned to an older supported major (10).</summary>
public sealed class MariaDb10Fixture : MariaDbLikeFixture
{
    public override string EngineName => "MariaDB";
    public override string ProviderName => "MariaDb";
    public override int LowestSupportedMajor => 10;

    protected override ContainerEngine Engine => ContainerEngine.MariaDb;

    protected override IDatabaseContainer BuildContainer() =>
        new MariaDbBuilder(new DockerImage("mariadb:10"))
            .WithUsername("root")
            .Build();
}

/// <summary>A MySQL fixture pinned to an older supported major (8).</summary>
public sealed class MySql8Fixture : MariaDbLikeFixture
{
    public override string EngineName => "MySQL";
    public override string ProviderName => "MySql";
    public override int LowestSupportedMajor => 8;

    protected override ContainerEngine Engine => ContainerEngine.MySql;

    protected override IDatabaseContainer BuildContainer() =>
        new MySqlBuilder(new DockerImage("mysql:8.0"))
            .WithUsername("root")
            .Build();
}
