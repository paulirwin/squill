using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.DeploymentScriptTest;

/// <summary>
/// Full round trip for pre/post-deployment scripts (issue #67) against real PostgreSQL:
/// build a DACPAC carrying deploy scripts, deploy it, and assert the scripts actually
/// ran against the database in the right order relative to the schema changes.
/// </summary>
public class PostgresDeploymentScriptTest : PostgresIntegrationTestBase
{
    private const string Schema = """
CREATE TABLE country
(
    id integer PRIMARY KEY,
    name varchar(100) NOT NULL
);
""";

    // The post-deployment script seeds the table the schema declares. This is the
    // motivating use case from the issue: the script must run after the table exists.
    private const string PostDeploy = """
INSERT INTO country (id, name) VALUES (1, 'Canada')
ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name;
INSERT INTO country (id, name) VALUES (2, 'Japan')
ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name;
""";

    [Fact]
    public async Task Deploy_RunsPostDeployScript_AfterSchemaChanges()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-postdeploy-integration");
        try
        {
            var dacpacPath = await BuildDacpacAsync(
                tempDir.FullName, ct, postDeploy: PostDeploy);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_postdeploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var messages = new List<string>();

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    progress: new CollectingProgress(messages), cancellationToken: ct);

                Assert.True(result.WasExecuted);
                Assert.Contains(messages, m => m.Contains("post-deployment", StringComparison.OrdinalIgnoreCase));

                // The seed rows prove the script ran, and that it ran *after* the table
                // was created — it would have failed outright otherwise.
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

    // Redeploying an unchanged DACPAC produces no schema deltas, but the deploy scripts
    // must still run — otherwise seeding an already-current database silently does nothing.
    [Fact]
    public async Task Deploy_RunsPostDeployScript_EvenWhenSchemaIsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-postdeploy-idempotent-integration");
        try
        {
            var dacpacPath = await BuildDacpacAsync(
                tempDir.FullName, ct, postDeploy: PostDeploy);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_postdeploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                // First deploy creates the schema and seeds it.
                await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false, cancellationToken: ct);

                // Wipe the seed data, leaving the schema exactly matching the DACPAC.
                await createdDb.ConnectAsync(ct);
                await createdDb.RunScriptAsync("DELETE FROM country;", cancellationToken: ct);
                Assert.Empty(await QueryCountryNamesAsync(targetDbName, ct));

                // Second deploy has zero schema deltas but must still re-run the seed.
                var messages = new List<string>();
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    progress: new CollectingProgress(messages), cancellationToken: ct);

                Assert.True(result.WasExecuted);

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

    // The pre-deployment script runs before the schema diff is applied.
    [Fact]
    public async Task Deploy_RunsPreDeployScript_BeforeSchemaChanges()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-predeploy-integration");
        try
        {
            // The pre-deploy script creates an audit table and stamps it. If it ran after
            // the schema phase this would still succeed, so the post-deploy script below
            // records the ordering by observing whether 'country' exists yet.
            var dacpacPath = await BuildDacpacAsync(
                tempDir.FullName, ct,
                preDeploy: """
CREATE TABLE IF NOT EXISTS deploy_audit
(
    phase varchar(20) PRIMARY KEY,
    country_existed boolean NOT NULL
);
INSERT INTO deploy_audit (phase, country_existed)
VALUES ('pre', to_regclass('public.country') IS NOT NULL)
ON CONFLICT (phase) DO UPDATE SET country_existed = EXCLUDED.country_existed;
""",
                postDeploy: """
INSERT INTO deploy_audit (phase, country_existed)
VALUES ('post', to_regclass('public.country') IS NOT NULL)
ON CONFLICT (phase) DO UPDATE SET country_existed = EXCLUDED.country_existed;
""");

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_predeploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var messages = new List<string>();

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    progress: new CollectingProgress(messages), cancellationToken: ct);

                Assert.True(result.WasExecuted);
                Assert.Contains(messages, m => m.Contains("pre-deployment", StringComparison.OrdinalIgnoreCase));

                // 'country' must not have existed during the pre phase, and must exist
                // during the post phase — proving the scripts bracket the schema changes.
                Assert.False(await QueryAuditAsync(targetDbName, "pre", ct),
                    "The pre-deployment script must run before the schema changes are applied.");
                Assert.True(await QueryAuditAsync(targetDbName, "post", ct),
                    "The post-deployment script must run after the schema changes are applied.");
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

    // A dry run must not execute the deploy scripts, but must include them in the
    // script it returns so the preview is faithful.
    [Fact]
    public async Task Deploy_DryRun_IncludesScriptsButDoesNotRunThem()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-deployscript-dryrun-integration");
        try
        {
            var dacpacPath = await BuildDacpacAsync(
                tempDir.FullName, ct, postDeploy: PostDeploy);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_dryrun_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: true, cancellationToken: ct);

                Assert.False(result.WasExecuted);
                Assert.Contains("INSERT INTO country", result.Script);
                Assert.Contains("CREATE TABLE", result.Script);

                // Nothing ran: the target is still empty.
                var untouched = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.DoesNotContain(untouched.Elements, e => e.Type == PostgresElementTypes.SqlTable);
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

    // The composed script from `squill script` must be valid, executable Postgres —
    // schema changes and seed data together.
    [Fact]
    public async Task ScriptFromFile_ComposedScript_IsExecutable()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-deployscript-script-integration");
        try
        {
            var dacpacPath = await BuildDacpacAsync(
                tempDir.FullName, ct, postDeploy: PostDeploy);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_script_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.ScriptFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, cancellationToken: ct);

                Assert.False(result.WasExecuted);

                // Running the emitted script by hand must produce both schema and data.
                await createdDb.ConnectAsync(ct);
                await createdDb.RunScriptAsync(result.Script, cancellationToken: ct);

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

    private async Task<List<string>> QueryCountryNamesAsync(string databaseName, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(ConnectionString) { Database = databaseName }.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand("SELECT name FROM country ORDER BY id;", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var names = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<bool> QueryAuditAsync(string databaseName, string phase, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(ConnectionString) { Database = databaseName }.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(
            "SELECT country_existed FROM deploy_audit WHERE phase = @phase;", connection);
        command.Parameters.AddWithValue("phase", phase);

        var value = await command.ExecuteScalarAsync(ct);

        Assert.NotNull(value);

        return (bool)value!;
    }

    private static async Task<string> BuildDacpacAsync(
        string dir,
        CancellationToken ct,
        string preDeploy = "",
        string postDeploy = "")
    {
        var sqlPath = Path.Combine(dir, "Schema.sql");
        await File.WriteAllTextAsync(sqlPath, Schema, ct);

        var dacpacPath = Path.Combine(dir, "bin", "TestDb.dacpac");
        var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            Name = "TestDb",
            PreDeployScript = preDeploy,
            PostDeployScript = postDeploy,
        };
        await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

        return dacpacPath;
    }

    private sealed class CollectingProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
