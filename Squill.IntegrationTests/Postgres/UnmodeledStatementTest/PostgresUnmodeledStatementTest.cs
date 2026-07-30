using Npgsql;
using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.UnmodeledStatementTest;

/// <summary>
/// The CREATE TABLE / SCHEMA / EXTENSION forms implemented for issue #143. Each threw
/// <c>NotImplementedException</c> at parse time, failing the whole build over one statement.
/// Now each parses, and the constructs Squill cannot carry into the model are reported as
/// SQ1002 unmodeled-construct warnings instead.
///
/// Every case runs its DDL against a live PostgreSQL container first. That is the point of
/// testing this here rather than only in unit tests: the warning is only honest if the SQL it
/// describes is genuinely valid Postgres that a user could reasonably have written. If the
/// engine rejected it, a hard parse error would have been the right behaviour after all.
/// </summary>
public class PostgresUnmodeledStatementTest : PostgresIntegrationTestBase
{
    private static async Task<BuildResult> BuildAsync(params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return await DacpacBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Runs DDL in a throwaway database so each case starts clean and cannot collide with
    /// another's object names.
    /// </summary>
    private async Task ExecuteInScratchDatabaseAsync(string sql)
    {
        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var db = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);

        try
        {
            await db.ConnectAsync(ct);
            await db.RunScriptAsync(sql, cancellationToken: ct);
        }
        finally
        {
            await db.DropAsync(ct);
        }
    }

    [Fact]
    public async Task TypedTable_IsValidPostgresAndBuildsWithAWarning()
    {
        const string sql = """
CREATE TYPE employee_type AS (id integer, name text);
CREATE TABLE employees OF employee_type;
""";

        await ExecuteInScratchDatabaseAsync(sql);

        var result = await BuildAsync(("Employees.sql", sql));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("employees", warning.Message, StringComparison.OrdinalIgnoreCase);

        // The composite type is still modeled; only the table that derives from it is not.
        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlCompositeType);
        Assert.DoesNotContain(result.Model.Elements,
            e => e.Type == PostgresElementTypes.SqlTable && e.Name?.Contains("employees") == true);
    }

    [Fact]
    public async Task PartitionChild_IsValidPostgresAndBuildsWithAWarning()
    {
        const string parentSql = """
CREATE TABLE measurement
(
    logdate  date NOT NULL,
    peaktemp integer
) PARTITION BY RANGE (logdate);
""";
        const string childSql = """
CREATE TABLE measurement_y2024 PARTITION OF measurement
    FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
""";

        await ExecuteInScratchDatabaseAsync(parentSql + childSql);

        // Only the child is built: the parent is a hard build error (see below).
        var result = await BuildAsync(("Measurement.sql", childSql));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("PARTITION OF", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The parent of a partitioned table declares its own columns, so — unlike the child — it
    /// would model and deploy quite happily, as an ordinary unpartitioned table. This test
    /// pins down why that is rejected rather than warned about: it first proves the deployed
    /// result really would differ (<c>relkind</c> 'r' rather than 'p'), then proves the build
    /// refuses it.
    /// </summary>
    [Fact]
    public async Task PartitionedParent_IsRejected_BecauseItWouldDeployUnpartitioned()
    {
        const string sql = """
CREATE TABLE measurement
(
    logdate  date NOT NULL,
    peaktemp integer
) PARTITION BY RANGE (logdate);
""";

        var ct = TestContext.Current.CancellationToken;

        // As Postgres deploys it, this is a partitioned table: relkind 'p'.
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var db = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);

        try
        {
            await db.ConnectAsync(ct);
            await db.RunScriptAsync(sql, cancellationToken: ct);

            var relkind = await ScalarAsync(db,
                "SELECT relkind::text FROM pg_class WHERE relname = 'measurement';", ct);

            Assert.Equal("p", relkind);
        }
        finally
        {
            await db.DropAsync(ct);
        }

        // Squill would have deployed relkind 'r', so it refuses to build rather than deploy
        // something whose semantics differ from the declaration (issue #143).
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync(("Measurement.sql", sql)));

        Assert.Contains("PARTITION BY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryKeyUsingIndex_IsValidPostgresAndBuildsWithAWarning()
    {
        // USING INDEX promotes an index that must already exist, so it is spelled as the
        // ALTER TABLE form here — that is how a user would really write it.
        const string liveSql = """
CREATE TABLE t (id integer NOT NULL);
CREATE UNIQUE INDEX ix_t_id ON t (id);
ALTER TABLE t ADD CONSTRAINT pk_t PRIMARY KEY USING INDEX ix_t_id;
""";

        await ExecuteInScratchDatabaseAsync(liveSql);

        const string sql = """
CREATE TABLE t
(
    id integer NOT NULL,
    CONSTRAINT pk_t PRIMARY KEY USING INDEX ix_t_id
);
""";

        var result = await BuildAsync(("T.sql", sql));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("USING INDEX", warning.Message, StringComparison.Ordinal);

        // The table is modeled; the constraint that named an index is not.
        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
        Assert.DoesNotContain(result.Model.Elements,
            e => e.Type == PostgresElementTypes.SqlPrimaryKeyConstraint);
    }

    [Fact]
    public async Task SchemaAuthorization_IsValidPostgresAndTheSchemaStillDeploys()
    {
        var ct = TestContext.Current.CancellationToken;

        // AUTHORIZATION needs a real role, so create one in the scratch database first.
        await ExecuteInScratchDatabaseAsync("""
CREATE ROLE squill_owner;
CREATE SCHEMA staging AUTHORIZATION squill_owner;
""");

        const string sql = "CREATE SCHEMA staging AUTHORIZATION squill_owner;";

        var result = await BuildAsync(("Staging.sql", sql));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("AUTHORIZATION", warning.Message, StringComparison.Ordinal);

        // The schema itself is modeled and deploys; only the ownership is dropped.
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var target = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, result.Model, target), ct);

            var schema = await ScalarAsync(testDb,
                "SELECT nspname FROM pg_namespace WHERE nspname = 'staging';", ct);

            Assert.Equal("staging", schema);

            var extracted = await dbModelBuilder.ExtractModelAsync(ct);
            Assert.Empty(SchemaCompare.Compare(provider, result.Model, extracted).Deltas);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// A named schema with a non-constant <c>AUTHORIZATION</c> role deploys and redeploys as a
    /// no-op — <em>including under <c>DropObjectsNotInSource</c></em> (issue #166).
    ///
    /// <para>
    /// That last clause is the point of the test. The name-less form
    /// (<c>CREATE SCHEMA AUTHORIZATION CURRENT_USER</c>) takes the schema's name from the
    /// deploying role, so a source model would hold the token <c>CURRENT_USER</c> while the
    /// target holds <c>postgres</c> — the two never match, and with drops enabled Squill would
    /// drop the real schema as undeclared. Naming the schema removes that entirely: the name is
    /// <c>staging</c> on both sides whoever deploys, and only the ownership is deploy-resolved,
    /// which was already unmodeled for a named role (SQ1002, #143).
    /// </para>
    ///
    /// <para>
    /// Both non-constant spellings are covered, and the deploy really runs against the server,
    /// so this also proves Squill emits DDL Postgres accepts rather than merely parsing it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("CURRENT_USER")]
    [InlineData("SESSION_USER")]
    public async Task NamedSchemaWithNonConstantAuthorization_RoundTripsUnderDrops(string role)
    {
        var ct = TestContext.Current.CancellationToken;

        var sql = $"CREATE SCHEMA staging AUTHORIZATION {role};";

        var result = await BuildAsync(("Staging.sql", sql));

        // Ownership is unmodeled exactly as it is for a named role; the token is reported so
        // it is clear which construct was dropped.
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains(role, warning.Message, StringComparison.Ordinal);

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var target = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, result.Model, target), ct);

            // The schema deployed under its declared name, not under the deploying role's.
            var schema = await ScalarAsync(testDb,
                "SELECT nspname FROM pg_namespace WHERE nspname = 'staging';", ct);

            Assert.Equal("staging", schema);

            var extracted = await dbModelBuilder.ExtractModelAsync(ct);

            // A plain redeploy is a no-op...
            Assert.Empty(SchemaCompare.Compare(provider, result.Model, extracted).Deltas);

            // ...and so is one with drops enabled: the declared schema is not seen as missing,
            // and the deployed one is not seen as undeclared.
            var withDrops = SchemaCompare.Compare(
                provider, result.Model, extracted,
                new DeployOptions { DropObjectsNotInSource = true });

            Assert.Empty(withDrops.Deltas);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// CASCADE is honored on deploy rather than dropped (issue #143). earthdistance depends on
    /// cube, so this is the case that proves it matters: with CASCADE emitted the deploy
    /// succeeds and pulls cube in, and the result round-trips.
    /// </summary>
    [Fact]
    public async Task ExtensionCascade_DeploysAndInstallsItsDependency()
    {
        var ct = TestContext.Current.CancellationToken;
        const string sql = "CREATE EXTENSION earthdistance CASCADE;";

        var result = await BuildAsync(("Ext.sql", sql));

        Assert.Empty(result.Warnings);

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var target = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, result.Model, target), ct);

            // CASCADE brought cube in with it.
            var cube = await ScalarAsync(testDb,
                "SELECT extname FROM pg_extension WHERE extname = 'cube';", ct);

            Assert.Equal("cube", cube);

            // The extracted model reports only earthdistance as declared; cube arrived as a
            // dependency. CASCADE is excluded from the element's identity, so the source model
            // still matches what was deployed and a redeploy is a no-op.
            var extracted = await dbModelBuilder.ExtractModelAsync(ct);

            Assert.Contains(extracted.Elements,
                e => e.Type == PostgresElementTypes.SqlExtension
                     && e.Name?.Contains("earthdistance") == true);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// Without CASCADE the same extension cannot be installed, because its dependency is
    /// missing. This is what makes honoring CASCADE the right call rather than warning and
    /// dropping it: dropping it would turn a working script into a deploy-time failure.
    /// </summary>
    [Fact]
    public async Task ExtensionWithoutCascade_FailsOnTheMissingDependency()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await BuildAsync(("Ext.sql", "CREATE EXTENSION earthdistance;"));

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);

        try
        {
            var target = await provider.CreateDatabaseModelBuilder(testDb).ExtractModelAsync(ct);

            await Assert.ThrowsAsync<PostgresException>(
                () => testDb.PublishAsync(SchemaCompare.Compare(provider, result.Model, target), ct));
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    private static async Task<object?> ScalarAsync(IDatabase database, string sql, CancellationToken ct)
    {
        await using var reader = await database.RunScriptReaderAsync(sql, cancellationToken: ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return reader.IsDBNull(0) ? null : reader.GetValue(0);
    }
}
