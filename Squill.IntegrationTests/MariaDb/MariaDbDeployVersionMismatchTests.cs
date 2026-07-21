using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.MariaDb;
using Testcontainers.MariaDb;
using Testcontainers.MySql;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// The deploy-time target-version mismatch (issue #39) for the MariaDB/MySQL provider,
/// exercised against a server pinned to an older supported major while the DACPAC targets a
/// newer supported major. Pinning makes the mismatch deterministic rather than depending on
/// what <c>:latest</c> happens to be. Runs once per engine via the two concrete classes below.
/// </summary>
public abstract class MariaDbDeployVersionMismatchTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    /// <summary>The provider name recorded in the DACPAC (<c>MariaDb</c> or <c>MySql</c>).</summary>
    protected abstract string ProviderName { get; }

    /// <summary>The engine's display name, as it appears in the mismatch exception.</summary>
    protected abstract string EngineName { get; }

    /// <summary>The pinned server's major version.</summary>
    protected abstract int ServerMajor { get; }

    /// <summary>A supported major newer than <see cref="ServerMajor"/> for the DACPAC to target.</summary>
    protected abstract int TargetMajor { get; }

    private const string SchemaSql = """
        CREATE TABLE customer
        (
            id   int NOT NULL AUTO_INCREMENT PRIMARY KEY,
            name varchar(100) NOT NULL
        );
        """;

    [Fact]
    public async Task Deploy_FailsWhenTargetVersionExceedsServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mariadb-version-mismatch");

        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "schema.sql");
            await File.WriteAllTextAsync(sqlPath, SchemaSql, ct);

            var workspace = DacpacBuilder.CreateWorkspace(new[] { sqlPath });
            var metadata = new ModelMetadata
            {
                Name = "TestDb",
                Version = "1.0.0.0",
                ProviderName = ProviderName,
                TargetMajorVersion = TargetMajor,
            };

            var dacpacPath = Path.Combine(tempDir.FullName, "TestDb.dacpac");
            await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var ex = await Assert.ThrowsAsync<TargetVersionMismatchException>(() =>
                    DacpacDeployer.DeployFromFileAsync(
                        dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                        cancellationToken: ct));

                Assert.Equal(TargetMajor, ex.RequiredMajorVersion);
                Assert.Equal(ServerMajor, ex.ActualMajorVersion);
                Assert.Equal(EngineName, ex.EngineName);

                // The check runs before any DDL, so the target must be untouched.
                var untouched = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);
                Assert.DoesNotContain(untouched.Elements, e => e.Type == MariaDbElementTypes.SqlTable);
            }
            finally
            {
                await createdDb.DropAsync(ct);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}

/// <summary>A MariaDB fixture pinned to an older supported major (10).</summary>
public sealed class MariaDb10Fixture : MariaDbLikeFixture
{
    public override string EngineName => "MariaDB";

    public override string ProviderName => "MariaDb";

    public override int LowestSupportedMajor => 10;

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

    protected override IDatabaseContainer BuildContainer() =>
        new MySqlBuilder(new DockerImage("mysql:8.0"))
            .WithUsername("root")
            .Build();
}

public sealed class MariaDbDeployVersionMismatchTestsMariaDb(MariaDb10Fixture fixture)
    : MariaDbDeployVersionMismatchTests, IClassFixture<MariaDb10Fixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
    protected override string ProviderName => "MariaDb";
    protected override string EngineName => "MariaDB";
    protected override int ServerMajor => 10;
    protected override int TargetMajor => 12; // newest supported MariaDB major
}

public sealed class MariaDbDeployVersionMismatchTestsMySql(MySql8Fixture fixture)
    : MariaDbDeployVersionMismatchTests, IClassFixture<MySql8Fixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
    protected override string ProviderName => "MySql";
    protected override string EngineName => "MySQL";
    protected override int ServerMajor => 8;
    protected override int TargetMajor => 9; // newest supported MySQL major
}
