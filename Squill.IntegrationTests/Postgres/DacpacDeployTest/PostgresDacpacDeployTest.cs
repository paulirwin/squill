using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.DacpacDeployTest;

public class PostgresDacpacDeployTest : PostgresIntegrationTestBase
{
    // Full round trip through the deploy path (issue #19): build a real .dacpac file
    // the way the build path does, then deploy it to a real, existing Postgres database
    // via DacpacDeployer — the exact code path the `squill deploy` CLI verb uses — and
    // assert the deployed schema matches the DACPAC's model.
    //
    // This proves the deployer takes a Squill DACPAC and a connection string and
    // produces the schema described by the DACPAC against real Postgres, end to end.
    [Fact]
    public async Task DeployFromFile_DeploysDacpacSchema_ToTargetDatabase()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-deploy-integration");
        try
        {
            // Arrange: build a real .dacpac file from the schema fixture.
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct);

            Model dacpacModel;
            await using (var stream = File.OpenRead(dacpacPath))
            {
                (_, dacpacModel) = await DacpacSerializer.Deserialize(stream, ct);
            }

            // Create a fresh, empty target database to deploy into.
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                // Act: deploy the DACPAC to the existing database via the CLI's code path,
                // capturing the progress messages the CLI would print.
                var progressMessages = new List<string>();
                var progress = new CollectingProgress(progressMessages);

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    progress: progress, cancellationToken: ct);

                Assert.True(result.WasExecuted, "A non-dry-run deploy should execute the script.");
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "Deploying to an empty database should generate a non-empty script.");

                // The deploy should have reported per-object progress naming each created
                // table, so the user sees what is being done rather than a single message.
                Assert.Contains(progressMessages, m => m.Contains("Creating table") && m.Contains("customer"));
                Assert.Contains(progressMessages, m => m.Contains("Creating table") && m.Contains("orders"));

                // Assert: the deployed database's model matches the DACPAC's model.
                var dbModelBuilder = provider.CreateDatabaseModelBuilder(createdDb);
                var deployedModel = await dbModelBuilder.ExtractModelAsync(ct);

                Assert.Equal(
                    ElementHashMultiset(dacpacModel),
                    ElementHashMultiset(deployedModel));
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

    // A dry run scripts the changes but does not touch the target database.
    [Fact]
    public async Task Deploy_DryRun_ScriptsChanges_ButDoesNotExecute()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-deploy-dryrun-integration");
        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: true,
                    cancellationToken: ct);

                Assert.False(result.WasExecuted, "A dry run must not execute the script.");
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "A dry run should still generate the script it would have run.");

                // The database must remain empty — nothing was deployed.
                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.DoesNotContain(
                    deployedModel.Elements,
                    e => e.Type == PostgresElementTypes.SqlTable);
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

    // When no target database is given, the deployer falls back to the connection
    // string's Database — deploying into whatever database the connection points at.
    [Fact]
    public async Task Deploy_UsesConnectionStringDatabase_WhenNoTargetGiven()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-deploy-connstr-integration");
        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                // Point the connection string's Database at the target so the deployer
                // resolves it with no explicit --target-database.
                var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
                {
                    Database = targetDbName
                }.ConnectionString;

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, connectionString, targetDatabaseName: null, dryRun: false,
                    cancellationToken: ct);

                Assert.True(result.WasExecuted);

                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.Contains(
                    deployedModel.Elements,
                    e => e.Type == PostgresElementTypes.SqlTable);
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

    private static async Task<string> BuildDacpacAsync(string dir, CancellationToken ct)
    {
        var schema = await new EmbeddedResourceFile(
                "Squill.IntegrationTests.Postgres.DacpacDeployTest.Schema.sql", FileKind.Compile)
            .ReadAllTextAsync(ct);

        var sqlPath = Path.Combine(dir, "Schema.sql");
        await File.WriteAllTextAsync(sqlPath, schema, ct);

        var dacpacPath = Path.Combine(dir, "bin", "TestDb.dacpac");
        var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
        var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "TestDb" };
        await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

        return dacpacPath;
    }

    // The sorted multiset of each top-level element's hash, as hex strings so the
    // collection compares by value. Order-independent by construction — the parser and
    // the database model builder emit elements in different orders.
    private static List<string> ElementHashMultiset(Model model)
        => model.Elements
            .Select(e => Convert.ToHexString(e.Hash))
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();

    // An IProgress<string> that records every reported message, so tests can assert on
    // the progress the deployer surfaces.
    private sealed class CollectingProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
