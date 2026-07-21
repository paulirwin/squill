using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ExtensionAlterTest;

// End-to-end coverage for updating an extension's version (issue #36) against real
// Postgres. citext ships several versions in the stock postgres image (1.4 … 1.8), so a
// database can install an older version and be updated to a newer pinned one.
//
// Two things are proven: a source pinning WITH VERSION drives an ALTER EXTENSION ...
// UPDATE when the installed version differs, and a source that pins no version leaves the
// installed version unmanaged (no spurious ALTER), because SchemaCompare backfills it.
public class PostgresExtensionAlterTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task PinnedNewerVersion_UpdatesExtensionInPlace()
    {
        const string before = "CREATE EXTENSION citext WITH VERSION '1.6';";
        const string after = "CREATE EXTENSION citext WITH VERSION '1.7';";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-ext-alter-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_ext_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var beforeDacpac = await BuildDacpacAsync(tempDir.FullName, "before", before, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                await using (var conn = await OpenAsync(targetDbName, ct))
                {
                    Assert.Equal("1.6", await ScalarAsync(
                        conn, "SELECT extversion FROM pg_extension WHERE extname = 'citext';"));
                }

                var afterDacpac = await BuildDacpacAsync(tempDir.FullName, "after", after, ct);
                var result = await DacpacDeployer.DeployFromFileAsync(
                    afterDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                Assert.True(result.WasExecuted);
                Assert.Contains("ALTER EXTENSION \"citext\" UPDATE TO '1.7';", result.Script);

                await using (var conn = await OpenAsync(targetDbName, ct))
                {
                    Assert.Equal("1.7", await ScalarAsync(
                        conn, "SELECT extversion FROM pg_extension WHERE extname = 'citext';"));
                }
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

    [Fact]
    public async Task UnpinnedVersion_LeavesInstalledVersionUnmanaged()
    {
        // The database has citext 1.6 installed; the source declares citext with no
        // version. The installed version is unmanaged, so a redeploy must produce no
        // change (no ALTER EXTENSION), not try to "correct" the version.
        const string before = "CREATE EXTENSION citext WITH VERSION '1.6';";
        const string after = "CREATE EXTENSION citext;";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-ext-unmanaged-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_ext_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var beforeDacpac = await BuildDacpacAsync(tempDir.FullName, "before", before, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                var afterDacpac = await BuildDacpacAsync(tempDir.FullName, "after", after, ct);
                var result = await DacpacDeployer.DeployFromFileAsync(
                    afterDacpac, ConnectionString, targetDbName, dryRun: true, cancellationToken: ct);

                // A dry run over an unmanaged-version redeploy must find nothing to do.
                Assert.DoesNotContain("ALTER EXTENSION", result.Script ?? string.Empty);

                // The installed version is untouched.
                await using var conn = await OpenAsync(targetDbName, ct);
                Assert.Equal("1.6", await ScalarAsync(
                    conn, "SELECT extversion FROM pg_extension WHERE extname = 'citext';"));
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

    [Fact]
    public async Task VersionedExtension_RoundTrips_ModelHashesMatchAfterPublish()
    {
        // A parser-built model pinning WITH VERSION must hash-match the model extracted
        // from the database after publish, proving the stored version agrees on both sides.
        const string schema = "CREATE EXTENSION citext WITH VERSION '1.6';";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Ext.sql", FileKind.Compile, schema));
        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct)).Model;

        var extension = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlExtension);
        Assert.Equal("1.6", extension.GetProperty<string>(PostgresPropertyNames.Version));

        var testDb = await provider.CreateDatabaseAsync($"squill_ext_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);
            var comparison = SchemaCompare.Compare(provider, model, emptyModel);
            await testDb.PublishAsync(comparison, ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            var publishedExtension = Assert.Single(
                publishedModel.Elements, i => i.Type == PostgresElementTypes.SqlExtension);
            Assert.Equal("1.6", publishedExtension.GetProperty<string>(PostgresPropertyNames.Version));

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match for a versioned extension.");
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    private static async Task<string> BuildDacpacAsync(
        string dir, string label, string schema, CancellationToken ct)
    {
        var sqlPath = Path.Combine(dir, $"{label}.sql");
        await File.WriteAllTextAsync(sqlPath, schema, ct);

        var dacpacPath = Path.Combine(dir, "bin", $"{label}.dacpac");
        var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
        var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "TestDb" };
        await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

        return dacpacPath;
    }

    private async Task<NpgsqlConnection> OpenAsync(string databaseName, CancellationToken ct)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = databaseName
        }.ConnectionString;

        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
