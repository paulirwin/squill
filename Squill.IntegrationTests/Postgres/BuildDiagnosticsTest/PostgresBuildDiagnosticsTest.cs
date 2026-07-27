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
}
