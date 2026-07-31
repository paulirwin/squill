using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.MariaDb;
using Squill.TestFramework;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end coverage of the minor-version deploy gate (issue #189) against real MySQL servers
/// that differ only below the major: <c>8.0</c> and <c>8.4</c>.
///
/// <para>
/// The pair matters because the asymmetry is the whole feature. A DACPAC targeting <c>8.4</c>
/// must be refused on an 8.0 server, while one targeting <c>8.0</c> must still deploy happily to
/// 8.4 — a floor is satisfied from above, without limit. A major-only check (the behaviour before
/// this issue) cannot distinguish these two servers at all, so both halves are asserted here
/// rather than only the failing one.
/// </para>
/// </summary>
public abstract class MySqlMinorVersionGateTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    /// <summary>The connected server's minor version (0 for mysql:8.0, 4 for mysql:8.4).</summary>
    protected abstract int ServerMinor { get; }

    private const string SchemaSql = """
        CREATE TABLE customer
        (
            id   int NOT NULL AUTO_INCREMENT PRIMARY KEY,
            name varchar(100) NOT NULL
        );
        """;

    protected async Task<string> BuildDacpacAsync(
        string directory, TargetVersion targetVersion, CancellationToken ct)
    {
        var sqlPath = Path.Combine(directory, "schema.sql");
        await File.WriteAllTextAsync(sqlPath, SchemaSql, ct);

        var workspace = DacpacBuilder.CreateWorkspace(new[] { sqlPath });
        var metadata = new ModelMetadata
        {
            Name = "TestDb",
            Version = "1.0.0.0",
            ProviderName = "MySql",
            TargetVersion = targetVersion,
        };

        var dacpacPath = Path.Combine(directory, "TestDb.dacpac");
        await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

        return dacpacPath;
    }

    /// <summary>
    /// Confirms the server really is the minor this fixture claims. Without this the gate tests
    /// could both pass against the wrong image and prove nothing.
    /// </summary>
    [Fact]
    public async Task Fixture_RunsTheExpectedServerMinorVersion()
    {
        var ct = TestContext.Current.CancellationToken;

        IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var dbName = $"squill_minor_probe_{Guid.NewGuid():n}";
        var db = await provider.CreateDatabaseAsync(dbName, ct);

        try
        {
            await db.ConnectAsync(ct);
            var version = ((MariaDbDatabase)db).GetServerVersion();

            // Only the major and minor are pinned: the patch is whatever the image currently
            // ships (8.0.36, 8.0.37, ...) and asserting it would break on every image refresh.
            Assert.Equal(8, version.Major);
            Assert.Equal(ServerMinor, version.Minor);
        }
        finally
        {
            await db.DropAsync(ct);
        }
    }

    [Fact]
    public async Task Deploy_TargetingTheServerOwnMinor_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mysql-minor-gate");

        try
        {
            var dacpacPath = await BuildDacpacAsync(
                tempDir.FullName, new TargetVersion(8, ServerMinor), ct);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                var deployed = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.Contains(
                    deployed.Elements,
                    e => e.Type == MariaDbElementTypes.SqlTable);
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

    /// <summary>
    /// A floor below the server always deploys. On 8.0 this is the same-version case; on 8.4 it
    /// is the load-bearing half of the asymmetry — an old floor must not block a newer server.
    /// </summary>
    [Fact]
    public async Task Deploy_TargetingAnOlderMinorThanTheServer_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mysql-minor-gate");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, new TargetVersion(8, 0), ct);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                var deployed = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.Contains(
                    deployed.Elements,
                    e => e.Type == MariaDbElementTypes.SqlTable);
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

/// <summary>
/// The 8.0 half: a DACPAC targeting 8.4 is refused here, which a major-only gate would have
/// allowed through.
/// </summary>
public sealed class MySqlMinorVersionGateTests80(MySql8Fixture fixture)
    : MySqlMinorVersionGateTests, IClassFixture<MySql8Fixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;

    protected override int ServerMinor => 0;

    [Fact]
    public async Task Deploy_TargetingANewerMinorThanTheServer_IsRefusedBeforeAnyDdl()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mysql-minor-gate");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, new TargetVersion(8, 4), ct);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var ex = await Assert.ThrowsAsync<TargetVersionMismatchException>(() =>
                    DacpacDeployer.DeployFromFileAsync(
                        dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                        cancellationToken: ct));

                Assert.Equal(8, ex.RequiredMajorVersion);
                Assert.Equal(4, ex.RequiredMinorVersion);
                Assert.Equal(8, ex.ActualMajorVersion);
                Assert.Equal(0, ex.ActualMinorVersion);
                Assert.Equal("MySQL", ex.EngineName);

                // The gate runs before any DDL, so the target must be untouched.
                var untouched = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.DoesNotContain(
                    untouched.Elements,
                    e => e.Type == MariaDbElementTypes.SqlTable);
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

/// <summary>
/// The 8.4 half: the same 8.4-targeting package that the 8.0 server refuses deploys cleanly here,
/// and an 8.0-targeting one still does too.
/// </summary>
public sealed class MySqlMinorVersionGateTests84(MySql84Fixture fixture)
    : MySqlMinorVersionGateTests, IClassFixture<MySql84Fixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;

    protected override int ServerMinor => 4;
}
