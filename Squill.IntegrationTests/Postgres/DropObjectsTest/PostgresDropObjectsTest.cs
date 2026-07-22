using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.DropObjectsTest;

// End-to-end coverage for DROP support (SSDT's DropObjectsNotInSource) and the
// block-on-possible-data-loss guard (SSDT's BlockOnPossibleDataLoss), issues #36/#31,
// against real Postgres: deploy an initial schema, then deploy a smaller schema and
// assert objects are (or are not) dropped and that the data-loss guard blocks
// destructive changes by default.
public class PostgresDropObjectsTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task ExtraTable_NotDropped_ByDefault()
    {
        const string before = """
CREATE TABLE keep (id integer PRIMARY KEY);
CREATE TABLE gone (id integer PRIMARY KEY);
""";
        const string after = "CREATE TABLE keep (id integer PRIMARY KEY);";

        await RunAsync(before, after, DeployOptions.CreateDefault(), async conn =>
        {
            // The default keeps objects not in the source, so 'gone' must still exist.
            Assert.True(await TableExistsAsync(conn, "gone"));
            Assert.True(await TableExistsAsync(conn, "keep"));
        });
    }

    [Fact]
    public async Task ExtraTable_Dropped_WhenOptedInAndDataLossAllowed()
    {
        const string before = """
CREATE TABLE keep (id integer PRIMARY KEY);
CREATE TABLE gone (id integer PRIMARY KEY);
""";
        const string after = "CREATE TABLE keep (id integer PRIMARY KEY);";

        var options = new DeployOptions
        {
            DropObjectsNotInSource = true,
            BlockOnPossibleDataLoss = false,
        };

        await RunAsync(before, after, options, async conn =>
        {
            Assert.False(await TableExistsAsync(conn, "gone"));
            Assert.True(await TableExistsAsync(conn, "keep"));
        });
    }

    [Fact]
    public async Task DroppingTable_BlockedByDefault_LeavesTableIntact()
    {
        const string before = """
CREATE TABLE keep (id integer PRIMARY KEY);
CREATE TABLE gone (id integer PRIMARY KEY);
""";
        const string after = "CREATE TABLE keep (id integer PRIMARY KEY);";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-drop-block");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var dbName = $"squill_drop_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(dbName, ct);

            try
            {
                var beforeDacpac = await BuildDacpacAsync(tempDir.FullName, "before", before, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, dbName, cancellationToken: ct);

                var afterDacpac = await BuildDacpacAsync(tempDir.FullName, "after", after, ct);

                // Opt into dropping objects but leave the data-loss guard on: the table
                // drop must be blocked before any SQL runs.
                await Assert.ThrowsAsync<PossibleDataLossException>(() =>
                    DacpacDeployer.DeployFromFileAsync(
                        afterDacpac, ConnectionString, dbName,
                        options: new DeployOptions { DropObjectsNotInSource = true },
                        cancellationToken: ct));

                await using var conn = await OpenAsync(dbName, ct);
                Assert.True(await TableExistsAsync(conn, "gone"),
                    "The blocked deploy must leave the table intact.");
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
    public async Task DryRun_PreviewsDataLosingScript_WithoutBlocking()
    {
        // A dry run must still be able to preview a destructive script — the data-loss
        // block applies only to a real run, so the user can inspect what would happen.
        const string before = """
CREATE TABLE keep (id integer PRIMARY KEY);
CREATE TABLE gone (id integer PRIMARY KEY);
""";
        const string after = "CREATE TABLE keep (id integer PRIMARY KEY);";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-drop-dryrun");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var dbName = $"squill_drop_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(dbName, ct);

            try
            {
                var beforeDacpac = await BuildDacpacAsync(tempDir.FullName, "before", before, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, dbName, cancellationToken: ct);

                var afterDacpac = await BuildDacpacAsync(tempDir.FullName, "after", after, ct);

                // Default options (block on data loss, drop objects) + dry run: must not
                // throw, and must return the DROP script for inspection.
                var result = await DacpacDeployer.DeployFromFileAsync(
                    afterDacpac, ConnectionString, dbName, dryRun: true,
                    options: new DeployOptions { DropObjectsNotInSource = true },
                    cancellationToken: ct);

                Assert.False(result.WasExecuted);
                Assert.Contains("DROP TABLE \"gone\"", result.Script);

                // The table must still exist — a dry run touches nothing.
                await using var conn = await OpenAsync(dbName, ct);
                Assert.True(await TableExistsAsync(conn, "gone"));
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
    public async Task ExtraIndex_Dropped_WhenOptedIn_TableKept()
    {
        const string before = """
CREATE TABLE film (film_id integer PRIMARY KEY, title varchar(255));
CREATE INDEX idx_film_title ON film (title);
""";
        const string after = "CREATE TABLE film (film_id integer PRIMARY KEY, title varchar(255));";

        // Dropping an index loses no table data, so the default guard doesn't block it;
        // only DropObjectsNotInSource must be turned on.
        var options = new DeployOptions { DropObjectsNotInSource = true };

        await RunAsync(before, after, options, async conn =>
        {
            Assert.True(await TableExistsAsync(conn, "film"));
            Assert.False(await IndexExistsAsync(conn, "idx_film_title"));
        });
    }

    // Deploys `before`, then `after` with the given options, then runs the caller's
    // assertions against the resulting database.
    private async Task RunAsync(
        string before, string after, DeployOptions options, Func<NpgsqlConnection, Task> assertAsync)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-drop-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var dbName = $"squill_drop_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(dbName, ct);

            try
            {
                var beforeDacpac = await BuildDacpacAsync(tempDir.FullName, "before", before, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, dbName, cancellationToken: ct);

                var afterDacpac = await BuildDacpacAsync(tempDir.FullName, "after", after, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    afterDacpac, ConnectionString, dbName, options: options, cancellationToken: ct);

                await using var conn = await OpenAsync(dbName, ct);
                await assertAsync(conn);
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

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string table)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT to_regclass(@name) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("name", table);
        return (bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection conn, string index)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) > 0 FROM pg_indexes WHERE indexname = @name", conn);
        cmd.Parameters.AddWithValue("name", index);
        return (bool)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
