using MySqlConnector;
using Squill.Core;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Verifies that the build-time diagnostics added for issue #61 agree with what a real
/// MariaDB/MySQL server does. A build error is only worth having if the SQL it rejects would
/// genuinely have failed on deploy, so each rejected case is executed against a live server
/// to confirm the engine rejects it too — and the accepted cases are confirmed to deploy.
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbBuildDiagnosticsTests
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

    [Fact]
    public async Task ForeignKeyToNonUniqueColumn_IsRejectedByBuildAndByTheEngine()
    {
        const string authorSql = "CREATE TABLE fk_author (id INT PRIMARY KEY, code VARCHAR(10));";
        const string bookSql = """
CREATE TABLE fk_book
(
    id INT PRIMARY KEY,
    author_code VARCHAR(10),
    FOREIGN KEY (author_code) REFERENCES fk_author (code)
);
""";

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync(("Author.sql", authorSql), ("Book.sql", bookSql)));

        Assert.Equal("SQ0004", ex.Code);

        // The engine rejects it too — which is exactly what the build error prevents.
        await InDatabaseAsync(async connection =>
        {
            await ExecuteAsync(connection, authorSql);

            await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(connection, bookSql));
        });
    }

    [Fact]
    public async Task ForeignKeyBackedByUniqueKey_BuildsAndDeploys()
    {
        // The mirror image: what the build accepts, the engine must accept too.
        const string authorSql = """
CREATE TABLE uq_author
(
    id INT PRIMARY KEY,
    code VARCHAR(10),
    UNIQUE KEY uq_author_code (code)
);
""";
        const string bookSql = """
CREATE TABLE uq_book
(
    id INT PRIMARY KEY,
    author_code VARCHAR(10),
    FOREIGN KEY (author_code) REFERENCES uq_author (code)
);
""";

        var result = await BuildAsync(("Author.sql", authorSql), ("Book.sql", bookSql));

        Assert.Contains(result.Model.Elements,
            e => e.Type == MariaDbElementTypes.SqlForeignKeyConstraint);

        await InDatabaseAsync(async connection =>
        {
            await ExecuteAsync(connection, authorSql);
            await ExecuteAsync(connection, bookSql);
        });
    }

    [Fact]
    public async Task ForeignKeyToLeftmostPrefixOfCompositeKey_IsRejectedByBuild()
    {
        // The engines diverge here: MariaDB accepts a foreign key backed by the leftmost
        // prefix of an index, MySQL 8+ does not. The build enforces the stricter MySQL rule
        // so a DACPAC that builds deploys on either engine — this test documents that a
        // MariaDB-only-valid schema is rejected on purpose, not by oversight.
        const string parentSql = "CREATE TABLE pfx_parent (a INT, b INT, PRIMARY KEY (a, b));";
        const string childSql = """
CREATE TABLE pfx_child
(
    id INT PRIMARY KEY,
    a INT,
    FOREIGN KEY (a) REFERENCES pfx_parent (a)
);
""";

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync(("Parent.sql", parentSql), ("Child.sql", childSql)));

        Assert.Equal("SQ0004", ex.Code);

        // Adding the unique key the check asks for makes it deploy on both engines.
        const string fixedParentSql =
            "CREATE TABLE pfx_parent (a INT, b INT, PRIMARY KEY (a, b), UNIQUE KEY uq_a (a));";

        var result = await BuildAsync(("Parent.sql", fixedParentSql), ("Child.sql", childSql));

        Assert.Contains(result.Model.Elements,
            e => e.Type == MariaDbElementTypes.SqlForeignKeyConstraint);

        await InDatabaseAsync(async connection =>
        {
            await ExecuteAsync(connection, fixedParentSql);
            await ExecuteAsync(connection, childSql);
        });
    }

    [Fact]
    public async Task DuplicateTable_IsRejectedByBuildAndByTheEngine()
    {
        const string sql = "CREATE TABLE dup_book (id INT PRIMARY KEY);";

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync(("A.sql", sql), ("B.sql", sql)));

        Assert.Equal("SQ0003", ex.Code);

        await InDatabaseAsync(async connection =>
        {
            await ExecuteAsync(connection, sql);

            await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(connection, sql));
        });
    }

    [Fact]
    public async Task CreateView_IsModeledAndDeploys()
    {
        const string bookSql = "CREATE TABLE warn_book (id INT PRIMARY KEY, title VARCHAR(50));";
        const string viewSql = "CREATE VIEW v_warn_book AS SELECT id FROM warn_book;";

        var result = await BuildAsync(("Book.sql", bookSql), ("View.sql", viewSql));

        // A view is a modeled object as of issue #42: it reaches the DACPAC, so it no longer
        // warns that it would vanish from the build.
        Assert.Empty(result.Warnings);

        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);
        Assert.Contains(result.Model.Elements, e => e.Name == "v_warn_book");

        await InDatabaseAsync(async connection =>
        {
            await ExecuteAsync(connection, bookSql);
            await ExecuteAsync(connection, viewSql);
        });
    }
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbBuildDiagnosticsTestsMariaDb(MariaDbFixture fixture)
    : MariaDbBuildDiagnosticsTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbBuildDiagnosticsTestsMySql(MySqlFixture fixture)
    : MariaDbBuildDiagnosticsTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
