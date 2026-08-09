using Npgsql;
using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.BuildDiagnosticsTest;

/// <summary>
/// Verifies that the build-time diagnostics added for issue #61 agree with what a real
/// PostgreSQL server does. A build error is only worth having if the SQL it rejects would
/// genuinely have failed on deploy — so each case is executed against a live database to
/// confirm the engine rejects it too, and the accepted cases are confirmed to deploy.
/// </summary>
public class PostgresBuildDiagnosticsTest : PostgresIntegrationTestBase
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

    private async Task<PostgresException> ExecuteExpectingFailureAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);

        return await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ForeignKeyToNonUniqueColumn_IsRejectedByBuildAndByPostgres()
    {
        const string authorSql = "CREATE TABLE fk_author (id integer PRIMARY KEY, code varchar(10));";
        const string bookSql =
            "CREATE TABLE fk_book (id integer PRIMARY KEY, "
            + "author_code varchar(10) REFERENCES fk_author (code));";

        // The build rejects it...
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync(("Author.sql", authorSql), ("Book.sql", bookSql)));

        Assert.Equal("SQ0004", ex.Code);

        // ...and so would the server, which is the point of catching it early. Postgres
        // 42830 is invalid_foreign_key.
        var postgresException = await ExecuteExpectingFailureAsync($"{authorSql}\n{bookSql}");

        Assert.Equal("42830", postgresException.SqlState);
    }

    [Fact]
    public async Task ForeignKeyWithNoColumnListToTableWithoutPrimaryKey_IsRejectedByBuildAndByPostgres()
    {
        const string authorSql = "CREATE TABLE nopk_author (id integer NOT NULL);";
        const string bookSql =
            "CREATE TABLE nopk_book (id integer PRIMARY KEY, author_id integer REFERENCES nopk_author);";

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync(("Author.sql", authorSql), ("Book.sql", bookSql)));

        Assert.Equal("SQ0004", ex.Code);

        // Postgres reports this particular shape as 42704 (undefined_object) rather than
        // 42830 — there is no primary key for the reference to resolve to at all — but it
        // is rejected just the same, which is what matters.
        var postgresException = await ExecuteExpectingFailureAsync($"{authorSql}\n{bookSql}");

        Assert.Equal("42704", postgresException.SqlState);
    }

    [Fact]
    public async Task ForeignKeyBackedByUniqueIndex_BuildsAndDeploys()
    {
        // The mirror image: what the build accepts, the server must accept too — otherwise
        // the uniqueness check would be rejecting valid schemas.
        const string authorSql = "CREATE TABLE ux_author (id integer PRIMARY KEY, code varchar(10));";
        const string indexSql = "CREATE UNIQUE INDEX ux_author_code ON ux_author (code);";
        const string bookSql =
            "CREATE TABLE ux_book (id integer PRIMARY KEY, "
            + "author_code varchar(10) REFERENCES ux_author (code));";

        var result = await BuildAsync(
            ("Author.sql", authorSql), ("Index.sql", indexSql), ("Book.sql", bookSql));

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);

        // No exception means Postgres agrees the FK is valid.
        await ExecuteAsync($"{authorSql}\n{indexSql}\n{bookSql}");
    }

    [Fact]
    public async Task DuplicateTable_IsRejectedByBuildAndByPostgres()
    {
        const string sql = "CREATE TABLE dup_book (id integer PRIMARY KEY);";

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync(("A.sql", sql), ("B.sql", sql)));

        Assert.Equal("SQ0003", ex.Code);

        // 42P07 is duplicate_table.
        var postgresException = await ExecuteExpectingFailureAsync($"{sql}\n{sql}");

        Assert.Equal("42P07", postgresException.SqlState);
    }

    [Fact]
    public async Task FunctionDefault_WarnsAtBuildAndIsAbsentFromTheModel()
    {
        // An allowlisted function default such as now() is modeled as of issue #124. An
        // arbitrary call is not: Postgres may rewrite its stored form, so it could not be
        // trusted to round-trip and keeps the warning.
        const string sql = """
CREATE FUNCTION warn_default_fn() RETURNS integer LANGUAGE sql IMMUTABLE AS 'SELECT 1';

CREATE TABLE warn_event
(
    id integer PRIMARY KEY,
    created_at integer DEFAULT warn_default_fn()
);
""";

        var result = await BuildAsync(("Event.sql", sql));

        // The warning is the contract: the default is real SQL that deploys fine, but it is
        // not carried in the model, so it would not round-trip — hence the diagnostic.
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);

        await ExecuteAsync(sql);

        var columns = result.Model.Elements
            .Where(e => e.Type == PostgresElementTypes.SqlTable)
            .SelectMany(e => e.GetRelationship(PostgresRelationshipNames.Columns)?.Entries ?? [])
            .OfType<Element>()
            .ToList();

        var column = Assert.Single(columns,
            e => e.Name?.EndsWith("created_at", StringComparison.Ordinal) == true);

        Assert.Null(column.GetProperty<string>(PostgresPropertyNames.DefaultValue));
    }

    /// <summary>
    /// CREATE TEMPORARY TABLE is a build error (issue #204), and unlike the cases above the
    /// server accepts the DDL, so the justification is measured differently. What is shown
    /// here is that the table does not survive the session that created it: it is created in
    /// a pg_temp schema rather than the declared one, and the very next connection cannot see
    /// it. A deploy would create it, the next extraction would not find it, and the deploy
    /// after that would create it again, forever.
    /// </summary>
    [Fact]
    public async Task TemporaryTable_IsRejectedByBuildBecausePostgresDoesNotKeepIt()
    {
        const string sql = "CREATE TEMPORARY TABLE temp_scratch (id integer PRIMARY KEY);";

        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(("Scratch.sql", sql)));

        Assert.Contains("temporary", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The server accepts it: this is valid SQL, which is exactly why it would otherwise
        // have deployed silently as something the model cannot track. ExecuteAsync opens its
        // own connection and closes it, which ends the session and takes the table with it.
        await ExecuteAsync(sql);

        // A later connection finds nothing: not the table, and no row in the catalog the
        // model builder reads.
        var missing = await ExecuteExpectingFailureAsync("SELECT 1 FROM temp_scratch;");

        // 42P01 is undefined_table.
        Assert.Equal("42P01", missing.SqlState);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var query = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = 'temp_scratch';
            """, connection);

        // Measured on PostgreSQL 18: never in the declared schema. While the session lives the
        // table exists under a pg_temp_N schema instead, which is not a schema the source can
        // declare or the model can name.
        Assert.Equal(0L, await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// UNLOGGED is rejected too, for a weaker but still real reason: the table persists, so
    /// unlike TEMP it is visible to extraction, but its contents do not survive a crash and
    /// the model has nowhere to record the distinction. Deploying it logged would be a
    /// different table than the one declared, so it is rejected rather than silently altered.
    /// </summary>
    [Fact]
    public async Task UnloggedTable_IsRejectedByBuildAndPostgresRecordsThePersistenceItself()
    {
        const string sql = "CREATE UNLOGGED TABLE unlogged_staging (id integer PRIMARY KEY);";

        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(("Staging.sql", sql)));

        Assert.Contains("UNLOGGED", ex.Message, StringComparison.Ordinal);

        await ExecuteAsync(sql);

        // The server keeps the distinction the model cannot: relpersistence is 'u' for an
        // unlogged table and 'p' for a permanent one, so deploying this as an ordinary table
        // really would produce a different object than the source declares.
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var query = new NpgsqlCommand(
            "SELECT relpersistence FROM pg_class WHERE relname = 'unlogged_staging';", connection);

        Assert.Equal('u', await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
