using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ImperativeStatementTest;

/// <summary>
/// Authored ALTER/DROP/DML in a declarative source file is rejected at build time with SQ0006
/// (issue #125).
///
/// <para>
/// Every case runs its statement against a live PostgreSQL container first, and that is the
/// point of testing it here rather than only in unit tests. The rejection is only defensible
/// if the SQL is genuinely valid Postgres that a user could reasonably have written — if the
/// engine itself rejected it, a plain syntax error would have been the right answer and a
/// bespoke diagnostic would be over-engineering. The message says "this does not belong in a
/// declarative project", which is a claim about Squill, not about the SQL.
/// </para>
/// </summary>
public class PostgresImperativeStatementTest : PostgresIntegrationTestBase
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

    [Theory]
    [InlineData("ALTER TABLE t ADD COLUMN c integer;", "ALTER TABLE")]
    [InlineData("ALTER TABLE t DROP COLUMN name;", "ALTER TABLE")]
    [InlineData("DROP TABLE t;", "DROP TABLE")]
    [InlineData("DROP INDEX ix_t_name;", "DROP INDEX")]
    [InlineData("TRUNCATE TABLE t;", "TRUNCATE")]
    public async Task SchemaChangingStatement_IsValidPostgresButRejectedAtBuild(
        string sql, string expectedName)
    {
        // The setup that makes each statement runnable, proving the SQL really is valid.
        const string setup = """
CREATE TABLE t (id integer PRIMARY KEY, name varchar(50) NOT NULL);
CREATE INDEX ix_t_name ON t (name);
""";

        await ExecuteInScratchDatabaseAsync(setup + sql);

        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(("T.sql", sql)));

        Assert.Equal(SqlSourceException.ImperativeStatement, ex.Code);
        Assert.Contains(expectedName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("declarative", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INSERT INTO t (id, name) VALUES (1, 'a');", "INSERT")]
    [InlineData("UPDATE t SET name = 'b';", "UPDATE")]
    [InlineData("DELETE FROM t;", "DELETE")]
    public async Task DmlStatement_IsValidPostgresButRejectedAndPointsAtDeployScripts(
        string sql, string expectedName)
    {
        const string setup = "CREATE TABLE t (id integer PRIMARY KEY, name varchar(50) NOT NULL);";

        await ExecuteInScratchDatabaseAsync(setup + sql);

        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(("Seed.sql", sql)));

        Assert.Equal(SqlSourceException.ImperativeStatement, ex.Code);
        Assert.Contains(expectedName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("post-deploy", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A data-modifying CTE is valid Postgres that writes rows, so it must get the seed-data
    /// remedy rather than being told to express itself as CREATE — which it cannot be. The
    /// live run is what proves it really writes: classifying on the leading WITH alone would
    /// have called this a query.
    /// </summary>
    [Fact]
    public async Task DataModifyingCte_WritesRowsAndGetsTheDeployScriptRemedy()
    {
        var ct = TestContext.Current.CancellationToken;
        const string sql = "WITH x AS (SELECT 1 AS n) INSERT INTO t (id) SELECT n FROM x;";

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var db = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);

        try
        {
            await db.ConnectAsync(ct);
            await db.RunScriptAsync("CREATE TABLE t (id integer PRIMARY KEY);", cancellationToken: ct);
            await db.RunScriptAsync(sql, cancellationToken: ct);

            // It really did write a row — this is a data change, not a query.
            var count = await ScalarAsync(db, "SELECT count(*)::text FROM t;", ct);

            Assert.Equal("1", count);
        }
        finally
        {
            await db.DropAsync(ct);
        }

        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(("Seed.sql", sql)));

        Assert.Equal(SqlSourceException.ImperativeStatement, ex.Code);
        Assert.Contains("post-deploy", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("end-state", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The remedy the message gives has to actually work: the end-state the rejected ALTER
    /// describes, written as CREATE, must build and deploy to the same schema. Without this
    /// the diagnostic would be telling authors to do something unproven.
    /// </summary>
    [Fact]
    public async Task TheRemedyWorks_DeclaredEndStateDeploysWhatTheAlterWouldHave()
    {
        var ct = TestContext.Current.CancellationToken;

        // What a migration-minded author would write, and what Squill rejects.
        const string imperative = """
CREATE TABLE t (id integer PRIMARY KEY);
ALTER TABLE t ADD COLUMN name varchar(50) NOT NULL;
""";

        await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(("T.sql", imperative)));

        // The same end-state, declared. This is what the diagnostic tells them to write.
        const string declarative = """
CREATE TABLE t (id integer PRIMARY KEY, name varchar(50) NOT NULL);
""";

        var result = await BuildAsync(("T.sql", declarative));

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var target = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, result.Model, target), ct);

            // The column the ALTER wanted is there, and a redeploy is a no-op.
            var column = await ScalarAsync(testDb,
                "SELECT column_name FROM information_schema.columns "
                + "WHERE table_name = 't' AND column_name = 'name';", ct);

            Assert.Equal("name", column);

            var extracted = await dbModelBuilder.ExtractModelAsync(ct);
            Assert.Empty(SchemaCompare.Compare(provider, result.Model, extracted).Deltas);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// Squill generates the ALTER itself — which is the reason an authored one is redundant
    /// rather than merely unsupported. Deploying a changed declaration alters the existing
    /// table in place, exactly what the rejected statement was trying to do by hand.
    /// </summary>
    [Fact]
    public async Task SquillGeneratesTheAlterItself()
    {
        var ct = TestContext.Current.CancellationToken;

        var before = await BuildAsync(("T.sql", "CREATE TABLE t (id integer PRIMARY KEY);"));
        var after = await BuildAsync(("T.sql",
            "CREATE TABLE t (id integer PRIMARY KEY, name varchar(50) NOT NULL);"));

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, before.Model, empty), ct);

            // Redeploying the changed declaration onto the existing table: Squill scripts the
            // ALTER that the author was told not to write.
            var deployed = await dbModelBuilder.ExtractModelAsync(ct);
            var comparison = SchemaCompare.Compare(provider, after.Model, deployed);

            Assert.NotEmpty(comparison.Deltas);

            await testDb.PublishAsync(comparison, ct);

            var column = await ScalarAsync(testDb,
                "SELECT column_name FROM information_schema.columns "
                + "WHERE table_name = 't' AND column_name = 'name';", ct);

            Assert.Equal("name", column);
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
