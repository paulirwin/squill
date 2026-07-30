using MySqlConnector;
using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Verifies that the build-time target-version warning added for issue #142 agrees with what a
/// real server does. The warning is only worth having if the construct it reports would
/// genuinely have failed on the targeted version — so the server is pinned to the older
/// supported major of each engine (MariaDB 10, MySQL 8) and the same DDL is executed against it
/// to confirm it really is rejected there.
///
/// <para>
/// Pinning is what makes this deterministic: against <c>:latest</c> the statement would succeed
/// and prove nothing about the boundary. This mirrors the deploy-time version-mismatch test
/// (issue #39), which pins the same fixtures for the same reason. Runs once per engine via the
/// two concrete classes below, since a scenario must hold on both.
/// </para>
/// </summary>
public abstract class MariaDbTargetVersionFeatureTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    /// <summary>The schema provider for the pinned server's major version.</summary>
    protected abstract MariaDbFamilyDatabaseSchemaProvider SchemaProvider { get; }

    /// <summary>
    /// A table declaring a <c>VECTOR</c> column — added in MariaDB 11.7 and MySQL 9.0, so
    /// rejected by both pinned servers.
    /// </summary>
    private const string VectorTableSql = """
        CREATE TABLE tv_embedding
        (
            id BIGINT UNSIGNED NOT NULL PRIMARY KEY,
            v  VECTOR(4) NOT NULL
        );
        """;

    private async Task<BuildResult> BuildAsync(params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return await new ParserWorkspaceModelBuilder(
                workspace, new AntlrMariaDbParser(), SchemaProvider)
            .ExtractModelAsync(TestContext.Current.CancellationToken);
    }

    private async Task<MySqlException> ExecuteExpectingFailureAsync(string sql)
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = new MySqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new MySqlCommand(sql, connection);

        return await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteNonQueryAsync(ct));
    }

    /// <summary>
    /// The whole point of the warning: the build reports VECTOR against the pinned older major,
    /// and the server running that major really does refuse the same DDL. Without the second
    /// half this would only prove Squill is self-consistent.
    /// </summary>
    [Fact]
    public async Task Vector_WarnsAtBuildAndIsRejectedByTheServer()
    {
        var result = await BuildAsync(("Embedding.sql", VectorTableSql));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Contains("VECTOR", warning.Message, StringComparison.OrdinalIgnoreCase);

        // ...and the server it warned about agrees.
        await ExecuteExpectingFailureAsync(VectorTableSql);
    }

    /// <summary>
    /// Ordinary source on the same pinned server: no warning, and the table is still modeled —
    /// the version check must not report anything for source the target accepts.
    /// </summary>
    [Fact]
    public async Task OrdinarySource_BuildsWithoutWarning()
    {
        const string sql = """
            CREATE TABLE tv_account
            (
                id    INT NOT NULL PRIMARY KEY,
                email VARCHAR(255)
            );
            """;

        var result = await BuildAsync(("Account.sql", sql));

        Assert.Empty(result.Warnings);
        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);
    }
}

public sealed class MariaDbTargetVersionFeatureTestsMariaDb(MariaDb10Fixture fixture)
    : MariaDbTargetVersionFeatureTests, IClassFixture<MariaDb10Fixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;

    protected override MariaDbFamilyDatabaseSchemaProvider SchemaProvider { get; } =
        new MariaDb10DatabaseSchemaProvider();
}

public sealed class MariaDbTargetVersionFeatureTestsMySql(MySql8Fixture fixture)
    : MariaDbTargetVersionFeatureTests, IClassFixture<MySql8Fixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;

    protected override MariaDbFamilyDatabaseSchemaProvider SchemaProvider { get; } =
        new MySql8DatabaseSchemaProvider();
}
