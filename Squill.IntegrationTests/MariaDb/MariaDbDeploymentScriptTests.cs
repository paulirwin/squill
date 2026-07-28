using MySqlConnector;
using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Full round trip for pre/post-deployment scripts (issue #67) against real MariaDB and
/// MySQL: build a DACPAC carrying deploy scripts, deploy it, and assert the scripts ran
/// against the database in the right order relative to the schema changes.
/// </summary>
public abstract class MariaDbDeploymentScriptTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private const string SchemaSql = """
        CREATE TABLE country
        (
            id   int NOT NULL PRIMARY KEY,
            name varchar(100) NOT NULL
        );
        """;

    // Seeds the table the schema declares — the motivating use case from the issue.
    // Written to be idempotent, since deploy scripts run on every deploy.
    private const string PostDeploySql = """
        INSERT INTO country (id, name) VALUES (1, 'Canada')
        ON DUPLICATE KEY UPDATE name = VALUES(name);
        INSERT INTO country (id, name) VALUES (2, 'Japan')
        ON DUPLICATE KEY UPDATE name = VALUES(name);
        """;

    [Fact]
    public async Task Deploy_RunsPostDeployScript_AfterSchemaChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mariadb-postdeploy");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct, postDeploy: PostDeploySql);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_postdeploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var messages = new List<string>();

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    progress: new CollectingProgress(messages), cancellationToken: ct);

                Assert.True(result.WasExecuted);
                Assert.Contains(messages, m => m.Contains("post-deployment", StringComparison.OrdinalIgnoreCase));

                // The seed rows prove the script ran after the table existed.
                var names = await QueryCountryNamesAsync(targetDbName, ct);

                Assert.Equal(["Canada", "Japan"], names);
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

    // A redeploy of an unchanged DACPAC has no schema deltas, but must still run the
    // deploy scripts — otherwise seeding an already-current database does nothing.
    [Fact]
    public async Task Deploy_RunsPostDeployScript_EvenWhenSchemaIsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mariadb-postdeploy-idempotent");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct, postDeploy: PostDeploySql);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_postdeploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                await createdDb.ConnectAsync(ct);
                await createdDb.RunScriptAsync("DELETE FROM country;", cancellationToken: ct);
                Assert.Empty(await QueryCountryNamesAsync(targetDbName, ct));

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                Assert.True(result.WasExecuted);
                Assert.Equal(["Canada", "Japan"], await QueryCountryNamesAsync(targetDbName, ct));
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

    // The pre-deployment script must run before the schema diff is applied.
    [Fact]
    public async Task Deploy_RunsPreDeployScript_BeforeSchemaChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mariadb-predeploy");

        try
        {
            // Each phase records whether 'country' existed at the time it ran, so the
            // recorded values prove the scripts bracket the schema changes.
            var dacpacPath = await BuildDacpacAsync(
                tempDir.FullName, ct,
                preDeploy: """
                    CREATE TABLE IF NOT EXISTS deploy_audit
                    (
                        phase           varchar(20) NOT NULL PRIMARY KEY,
                        country_existed int NOT NULL
                    );
                    INSERT INTO deploy_audit (phase, country_existed)
                    SELECT 'pre', COUNT(*) FROM information_schema.tables
                    WHERE table_schema = DATABASE() AND table_name = 'country'
                    ON DUPLICATE KEY UPDATE country_existed = VALUES(country_existed);
                    """,
                postDeploy: """
                    INSERT INTO deploy_audit (phase, country_existed)
                    SELECT 'post', COUNT(*) FROM information_schema.tables
                    WHERE table_schema = DATABASE() AND table_name = 'country'
                    ON DUPLICATE KEY UPDATE country_existed = VALUES(country_existed);
                    """);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_predeploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var messages = new List<string>();

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    progress: new CollectingProgress(messages), cancellationToken: ct);

                Assert.True(result.WasExecuted);
                Assert.Contains(messages, m => m.Contains("pre-deployment", StringComparison.OrdinalIgnoreCase));

                Assert.Equal(0, await QueryAuditAsync(targetDbName, "pre", ct));
                Assert.Equal(1, await QueryAuditAsync(targetDbName, "post", ct));
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

    // A dry run includes the deploy scripts in the previewed script but runs nothing.
    [Fact]
    public async Task Deploy_DryRun_IncludesScriptsButDoesNotRunThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mariadb-deployscript-dryrun");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct, postDeploy: PostDeploySql);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_dryrun_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: true,
                    cancellationToken: ct);

                Assert.False(result.WasExecuted);
                Assert.Contains("INSERT INTO country", result.Script);

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

    private async Task<List<string>> QueryCountryNamesAsync(string databaseName, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(
            new MySqlConnectionStringBuilder(Fixture.ConnectionString) { Database = databaseName }
                .ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new MySqlCommand("SELECT name FROM country ORDER BY id;", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var names = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<int> QueryAuditAsync(string databaseName, string phase, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(
            new MySqlConnectionStringBuilder(Fixture.ConnectionString) { Database = databaseName }
                .ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new MySqlCommand(
            "SELECT country_existed FROM deploy_audit WHERE phase = @phase;", connection);
        command.Parameters.AddWithValue("phase", phase);

        var value = await command.ExecuteScalarAsync(ct);

        Assert.NotNull(value);

        return Convert.ToInt32(value);
    }

    private Task<string> BuildDacpacAsync(
        string directory,
        CancellationToken ct,
        string preDeploy = "",
        string postDeploy = "")
        => DacpacTestBuilder.BuildToFileAsync(
            directory,
            SchemaSql,
            Fixture.ProviderName,
            ws => new ParserWorkspaceModelBuilder(ws, new Squill.MariaDbParser.AntlrMariaDbParser(), Fixture.SchemaProviderOf()),
            ct,
            preDeploy: preDeploy,
            postDeploy: postDeploy);
}

public sealed class MariaDbDeploymentScriptTestsMariaDb(MariaDbFixture fixture)
    : MariaDbDeploymentScriptTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbDeploymentScriptTestsMySql(MySqlFixture fixture)
    : MariaDbDeploymentScriptTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
