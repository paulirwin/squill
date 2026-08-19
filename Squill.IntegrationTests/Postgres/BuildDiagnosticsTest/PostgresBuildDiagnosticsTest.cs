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

    // ---- CREATE TABLE storage clauses (issue #206) ----

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var query = new NpgsqlCommand(sql, connection);

        return await query.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The measurement the build's TABLESPACE rule rests on: naming <c>pg_default</c> is a
    /// genuine no-op, recording the same <c>reltablespace = 0</c> as declaring no tablespace at
    /// all. That is why the default spelling is accepted and dropped rather than rejected.
    /// </summary>
    [Fact]
    public async Task DefaultTablespaceAndAccessMethod_AreAcceptedAndRecordedIdenticallyToOmittingThem()
    {
        const string sql =
            "CREATE TABLE ts_spelled (id integer PRIMARY KEY) USING heap TABLESPACE pg_default;\n"
            + "CREATE TABLE ts_omitted (id integer PRIMARY KEY);";

        // The build accepts both spellings, with nothing to warn about.
        var result = await BuildAsync(("Tables.sql", sql));

        Assert.Empty(result.Warnings);

        await ExecuteAsync(sql);

        // And the server cannot tell them apart, which is what makes dropping the clause safe.
        Assert.Equal(
            1L,
            await ScalarAsync(
                """
                SELECT count(DISTINCT (reltablespace, relam))
                FROM pg_class WHERE relname IN ('ts_spelled', 'ts_omitted');
                """));
    }

    /// <summary>
    /// The counterpart, and the clause the issue calls most consequential. A non-default access
    /// method is rejected at build time; the server confirms the name is a real object it
    /// resolves, so accepting the declaration and deploying a heap table would have produced a
    /// different kind of table than the source asked for.
    /// </summary>
    [Fact]
    public async Task NonDefaultAccessMethod_IsRejectedByBuildAndIsNotSomethingPostgresIgnores()
    {
        const string sql = "CREATE TABLE am_columnar (id integer PRIMARY KEY) USING columnar;";

        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(("Columnar.sql", sql)));

        Assert.Contains("columnar", ex.Message, StringComparison.Ordinal);

        // Postgres resolves USING against pg_am rather than ignoring it: 42704 is
        // undefined_object, "access method \"columnar\" does not exist". So the clause always
        // means something to the server, and silently dropping it changed the deployed table.
        var postgresException = await ExecuteExpectingFailureAsync(sql);

        Assert.Equal("42704", postgresException.SqlState);
    }

    [Fact]
    public async Task NonDefaultTablespace_IsRejectedByBuild()
    {
        // Not executed against the server: creating a real tablespace needs a writable directory
        // on the container's filesystem. What matters here is the build decision, and the
        // accepted-default half above is what was measured.
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(
            ("Fast.sql", "CREATE TABLE ts_fast (id integer PRIMARY KEY) TABLESPACE fast_ssd;")));

        Assert.Contains("fast_ssd", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Storage parameters warn rather than reject, so the table must still deploy. The warning is
    /// honest about what happens: the server records the parameter the source asked for, and the
    /// model does not carry it, so a deploy from this model would leave it at the default.
    /// </summary>
    [Fact]
    public async Task StorageParameters_WarnAndTheTableStillDeploys()
    {
        const string sql =
            "CREATE TABLE wo_fillfactor (id integer PRIMARY KEY) WITH (fillfactor = 70);";

        var result = await BuildAsync(("Fillfactor.sql", sql));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("fillfactor", warning.Message, StringComparison.Ordinal);

        // The table is modeled, unlike the rejected cases above.
        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        await ExecuteAsync(sql);

        // The parameter really does persist, which is why it warrants a warning rather than
        // silence -- and why it cannot simply be modeled until table reloptions are extracted.
        Assert.Equal(
            "{fillfactor=70}",
            await ScalarAsync(
                "SELECT reloptions::text FROM pg_class WHERE relname = 'wo_fillfactor';"));
    }

    /// <summary>
    /// The default-spelling rule is case-sensitive for a quoted identifier and case-insensitive
    /// for an unquoted one, because that is what the server does. Both halves are checked
    /// against it here: the unquoted upper-case spellings are folded and create the table, while
    /// the quoted upper-case ones are undefined objects the server refuses outright (42704), so
    /// the build must refuse them too rather than accept a statement no deploy can run.
    /// </summary>
    [Fact]
    public async Task DefaultStorageClauseNames_FoldCaseOnlyWhenUnquoted()
    {
        // Unquoted: folded by the server, accepted by the build, and still a plain heap table in
        // the default tablespace.
        const string unquotedSql =
            "CREATE TABLE cs_unquoted (id integer PRIMARY KEY) USING HEAP TABLESPACE PG_DEFAULT;";

        var unquoted = await BuildAsync(("Unquoted.sql", unquotedSql));

        Assert.Empty(unquoted.Warnings);

        await ExecuteAsync(unquotedSql);

        Assert.Equal(
            "heap",
            await ScalarAsync(
                """
                SELECT am.amname FROM pg_class c JOIN pg_am am ON am.oid = c.relam
                WHERE c.relname = 'cs_unquoted';
                """));

        // Quoted: taken verbatim, so these name objects that do not exist. 42704 is
        // undefined_object for both -- `access method "HEAP" does not exist` and
        // `tablespace "PG_DEFAULT" does not exist`.
        foreach (var (file, sql, name) in new[]
                 {
                     ("QuotedAm.sql", """CREATE TABLE cs_am (id integer PRIMARY KEY) USING "HEAP";""", "HEAP"),
                     ("QuotedTs.sql",
                         """CREATE TABLE cs_ts (id integer PRIMARY KEY) TABLESPACE "PG_DEFAULT";""",
                         "PG_DEFAULT"),
                 })
        {
            var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync((file, sql)));

            Assert.Contains(name, ex.Message, StringComparison.Ordinal);

            var postgresException = await ExecuteExpectingFailureAsync(sql);

            Assert.Equal("42704", postgresException.SqlState);
        }
    }

    /// <summary>
    /// <c>ON COMMIT</c> is the fourth clause issue #206 lists, and it needs no handling of its
    /// own: the server accepts it only on a temporary table, and a temporary table is already
    /// rejected by the build. There is no declaration on which dropping it could change anything.
    /// </summary>
    [Fact]
    public async Task OnCommit_IsRejectedByPostgresOnAnOrdinaryTable()
    {
        var postgresException = await ExecuteExpectingFailureAsync(
            "CREATE TABLE oc_plain (id integer PRIMARY KEY) ON COMMIT DELETE ROWS;");

        // 42P16 is invalid_table_definition: "ON COMMIT can only be used on temporary tables".
        Assert.Equal("42P16", postgresException.SqlState);
    }
}
