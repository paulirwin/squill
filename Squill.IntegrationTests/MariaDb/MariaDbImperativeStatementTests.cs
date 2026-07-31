using MySqlConnector;
using Squill.Core;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Authored ALTER/DROP/DML in a declarative source file is rejected at build time with SQ0006
/// (issue #125).
///
/// <para>
/// Each statement is executed against a live server first, and that is the point of testing it
/// here rather than only in unit tests. The rejection is only defensible if the SQL is
/// genuinely valid — if the engine itself rejected it, a plain syntax error would have been
/// the right answer. The message claims the statement does not belong in a declarative
/// project, which is a statement about Squill, not about the SQL.
/// </para>
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbImperativeStatementTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private async Task<BuildResult> BuildAsync(params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return await DacpacBuilder.BuildModelAsync(
            workspace, Fixture.SchemaProviderOf(), TestContext.Current.CancellationToken);
    }

    // Runs the statements in a throwaway database, so each test starts from a clean schema.
    private async Task InDatabaseAsync(Func<MySqlConnection, Task> body)
    {
        var databaseName = $"squill_test_{Guid.NewGuid():n}";

        await using var connection = new MySqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var create = new MySqlCommand($"CREATE DATABASE `{databaseName}`;", connection))
        {
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            await connection.ChangeDatabaseAsync(databaseName, TestContext.Current.CancellationToken);

            await body(connection);
        }
        finally
        {
            await connection.ChangeDatabaseAsync("mysql", TestContext.Current.CancellationToken);

            await using var drop = new MySqlCommand($"DROP DATABASE `{databaseName}`;", connection);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private const string Setup = """
CREATE TABLE imp_t (id INT PRIMARY KEY, name VARCHAR(50) NOT NULL);
""";

    [Theory]
    [InlineData("ALTER TABLE imp_t ADD COLUMN c INT;", "ALTER TABLE")]
    [InlineData("ALTER TABLE imp_t DROP COLUMN name;", "ALTER TABLE")]
    [InlineData("DROP TABLE imp_t;", "DROP TABLE")]
    [InlineData("TRUNCATE TABLE imp_t;", "TRUNCATE")]
    public async Task SchemaChangingStatement_IsValidSqlButRejectedAtBuild(
        string sql, string expectedName)
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(("T.sql", sql)));

        Assert.Equal(SqlSourceException.ImperativeStatement, ex.Code);
        Assert.Contains(expectedName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("declarative", ex.Message, StringComparison.Ordinal);

        // The engine runs it happily — the build rejects it on declarative grounds, not
        // because the SQL is malformed.
        await InDatabaseAsync(async connection =>
        {
            await ExecuteAsync(connection, Setup);
            await ExecuteAsync(connection, sql);
        });
    }

    [Theory]
    [InlineData("INSERT INTO imp_t (id, name) VALUES (1, 'a');", "INSERT")]
    [InlineData("UPDATE imp_t SET name = 'b';", "UPDATE")]
    [InlineData("DELETE FROM imp_t;", "DELETE")]
    public async Task DmlStatement_IsValidSqlButRejectedAndPointsAtDeployScripts(
        string sql, string expectedName)
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(("Seed.sql", sql)));

        Assert.Equal(SqlSourceException.ImperativeStatement, ex.Code);
        Assert.Contains(expectedName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("post-deploy", ex.Message, StringComparison.Ordinal);

        await InDatabaseAsync(async connection =>
        {
            await ExecuteAsync(connection, Setup);
            await ExecuteAsync(connection, sql);
        });
    }

    /// <summary>
    /// The remedy the message gives has to actually work: the end-state the rejected ALTER
    /// describes, written as CREATE, must build and deploy to the same schema.
    /// </summary>
    [Fact]
    public async Task TheRemedyWorks_DeclaredEndStateDeploysWhatTheAlterWouldHave()
    {
        const string imperative = """
CREATE TABLE imp_remedy (id INT PRIMARY KEY);
ALTER TABLE imp_remedy ADD COLUMN name VARCHAR(50) NOT NULL;
""";

        await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(("T.sql", imperative)));

        // The same end-state, declared — what the diagnostic tells the author to write.
        const string declarative =
            "CREATE TABLE imp_remedy (id INT PRIMARY KEY, name VARCHAR(50) NOT NULL);";

        var result = await BuildAsync(("T.sql", declarative));

        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);

        await InDatabaseAsync(async connection =>
        {
            await ExecuteAsync(connection, declarative);

            await using var command = new MySqlCommand(
                "SELECT COLUMN_NAME FROM information_schema.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'imp_remedy' "
                + "AND COLUMN_NAME = 'name';",
                connection);

            var column = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            Assert.Equal("name", column);
        });
    }

    /// <summary>
    /// Declarative source is unaffected — this rejects imperative statements, it does not
    /// narrow what a project may declare.
    /// </summary>
    [Fact]
    public async Task DeclarativeSource_StillBuildsAndDeploys()
    {
        const string sql = """
CREATE TABLE imp_ok (id INT PRIMARY KEY, name VARCHAR(50) NOT NULL);
CREATE INDEX ix_imp_ok_name ON imp_ok (name);
""";

        var result = await BuildAsync(("T.sql", sql));

        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);
        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlIndex);

        await InDatabaseAsync(async connection => await ExecuteAsync(connection, sql));
    }
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbImperativeStatementTestsMariaDb(MariaDbFixture fixture)
    : MariaDbImperativeStatementTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbImperativeStatementTestsMySql(MySqlFixture fixture)
    : MariaDbImperativeStatementTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
