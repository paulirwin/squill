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

    // Full round trip through the script path (issue #21): build a real .dacpac, script a
    // deployment against a real, empty Postgres database via DacpacDeployer.ScriptFromFileAsync
    // — the code path the `squill script` CLI verb uses — assert the target is untouched
    // (scripting only reads schema), then prove the generated script is valid, executable
    // Postgres that produces the DACPAC's schema when run.
    [Fact]
    public async Task ScriptFromFile_GeneratesExecutableScript_WithoutTouchingTarget()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-script-integration");
        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct);

            Model dacpacModel;
            await using (var stream = File.OpenRead(dacpacPath))
            {
                (_, dacpacModel) = await DacpacSerializer.Deserialize(stream, ct);
            }

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_script_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                // Act: generate the deployment script against the empty target.
                var result = await DacpacDeployer.ScriptFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, cancellationToken: ct);

                Assert.False(result.WasExecuted,
                    "Scripting must not execute anything against the target.");
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "Scripting an empty target should generate a non-empty script.");

                // The target must remain empty — scripting only reads the schema.
                var untouchedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.DoesNotContain(
                    untouchedModel.Elements,
                    e => e.Type == PostgresElementTypes.SqlTable);

                // Prove the generated script is valid, executable Postgres: run it against
                // the target and assert the resulting schema matches the DACPAC's model.
                await createdDb.RunScriptAsync(result.Script, cancellationToken: ct);

                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

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

    // The oldest supported PostgreSQL major (see the Postgresql*DatabaseSchemaProvider types).
    // Any current test container satisfies it, so a DACPAC targeting it deploys normally.
    private const int LowestSupportedMajor = 14;

    // A DACPAC targeting a version the server satisfies deploys normally (issue #39).
    [Fact]
    public async Task Deploy_SucceedsWhenServerMeetsTargetVersion()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-deploy-version-ok-integration");
        try
        {
            var dacpacPath = await BuildDacpacAsync(
                tempDir.FullName, ct, targetMajorVersion: LowestSupportedMajor);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                Assert.True(result.WasExecuted);

                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);
                Assert.Contains(deployedModel.Elements, e => e.Type == PostgresElementTypes.SqlTable);
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

    private static async Task<string> BuildDacpacAsync(
        string dir, CancellationToken ct, int? targetMajorVersion = null)
    {
        var schema = await new EmbeddedResourceFile(
                "Squill.IntegrationTests.Postgres.DacpacDeployTest.Schema.sql", FileKind.Compile)
            .ReadAllTextAsync(ct);

        var sqlPath = Path.Combine(dir, "Schema.sql");
        await File.WriteAllTextAsync(sqlPath, schema, ct);

        var dacpacPath = Path.Combine(dir, "bin", "TestDb.dacpac");
        var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            Name = "TestDb",
            TargetMajorVersion = targetMajorVersion,
        };
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

/// <summary>
/// The deploy-time target-version mismatch (issue #39), exercised against a server pinned to
/// an older supported PostgreSQL (14) while the DACPAC targets the newest supported major (18).
/// Pinning makes the mismatch deterministic rather than depending on what <c>postgres:latest</c>
/// happens to be.
/// </summary>
public class PostgresDeployVersionMismatchTest : PostgresIntegrationTestBase
{
    // An older supported major (has a Postgresql14DatabaseSchemaProvider type).
    protected override string DockerImageName => "postgres:14";

    // The newest supported major (has a Postgresql18DatabaseSchemaProvider type).
    private const int NewestSupportedMajor = 18;

    [Fact]
    public async Task Deploy_FailsWhenTargetVersionExceedsServer()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-deploy-version-mismatch");
        try
        {
            var schema = await new EmbeddedResourceFile(
                    "Squill.IntegrationTests.Postgres.DacpacDeployTest.Schema.sql", FileKind.Compile)
                .ReadAllTextAsync(ct);
            var sqlPath = Path.Combine(tempDir.FullName, "Schema.sql");
            await File.WriteAllTextAsync(sqlPath, schema, ct);

            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "TestDb.dacpac");
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata
            {
                ProviderName = "Postgresql",
                Name = "TestDb",
                TargetMajorVersion = NewestSupportedMajor,
            };
            await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var ex = await Assert.ThrowsAsync<TargetVersionMismatchException>(() =>
                    DacpacDeployer.DeployFromFileAsync(
                        dacpacPath, ConnectionString, targetDbName, dryRun: false,
                        cancellationToken: ct));

                Assert.Equal(NewestSupportedMajor, ex.RequiredMajorVersion);
                Assert.Equal(14, ex.ActualMajorVersion);
                Assert.Equal("PostgreSQL", ex.EngineName);

                // The check runs before any DDL, so the target must be untouched.
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
}
