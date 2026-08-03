using Npgsql;
using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.TargetVersionFeatureTest;

/// <summary>
/// Verifies that the build-time target-version warning added for issue #142 agrees with what a
/// real PostgreSQL server does. The warning is only worth having if the construct it reports
/// would genuinely have failed on the targeted version — so the server is pinned to the older
/// major (14) and the same DDL is executed against it to confirm it really is rejected there.
///
/// <para>
/// Pinning is what makes this deterministic: against <c>postgres:latest</c> the statement would
/// succeed and prove nothing about the boundary. This mirrors the deploy-time version-mismatch
/// test (issue #39), which pins the same image for the same reason.
/// </para>
/// </summary>
public class PostgresTargetVersionFeatureTest : PostgresIntegrationTestBase
{
    // The oldest supported major, and the one before NULLS NOT DISTINCT was introduced.
    protected override string DockerImageName => "postgres:14";

    private const string NullsNotDistinctSql =
        "CREATE UNIQUE INDEX ix_tv_account_email ON tv_account (email) NULLS NOT DISTINCT;";

    private static async Task<BuildResult> BuildAsync(
        PostgresqlDatabaseSchemaProvider schemaProvider, params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return await DacpacBuilder.BuildModelAsync(
            workspace, schemaProvider, TestContext.Current.CancellationToken);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<PostgresException> ExecuteExpectingFailureAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);

        return await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The whole point of the warning: the build reports NULLS NOT DISTINCT against a target of
    /// 14, and the server running 14 really does refuse the same DDL. Without the second half
    /// this would only prove Squill is self-consistent.
    /// </summary>
    [Fact]
    public async Task NullsNotDistinct_WarnsAtBuildAndIsRejectedByPostgres14()
    {
        const string tableSql =
            "CREATE TABLE tv_account (id integer PRIMARY KEY, email text);";

        // The build warns...
        var result = await BuildAsync(
            new Postgresql14DatabaseSchemaProvider(),
            ("Account.sql", tableSql),
            ("Index.sql", NullsNotDistinctSql));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Contains("NULLS NOT DISTINCT", warning.Message);

        // ...and the server it warned about agrees, with a syntax error rather than merely
        // ignoring the clause — which is what makes this a build-time concern instead of
        // something that quietly deploys with different semantics.
        await ExecuteAsync(tableSql);

        var ex = await ExecuteExpectingFailureAsync(NullsNotDistinctSql);

        Assert.Equal("42601", ex.SqlState);
    }

    /// <summary>
    /// A negative numeric scale is accepted from PostgreSQL 15, and rejected by the pinned 14
    /// server (issue #191). Squill rejects it at build time on every version, but for a
    /// different reason than the version boundary — it cannot round-trip, since the catalog
    /// reports a -2 scale as 2046. This confirms the half of that story the pinned server can
    /// show: that 14 genuinely refuses the DDL.
    /// </summary>
    [Fact]
    public async Task NegativeNumericScale_IsRejectedByPostgres14()
    {
        var ex = await ExecuteExpectingFailureAsync(
            "CREATE TABLE tv_neg_scale (id integer PRIMARY KEY, rounded numeric(4, -2));");

        // 22023 invalid_parameter_value: "NUMERIC scale -2 must be between 0 and precision 4".
        Assert.Equal("22023", ex.SqlState);
    }

    /// <summary>
    /// The same source against a target that does support it: no warning, and the index still
    /// carries the property, so the warning never changed what was built.
    /// </summary>
    [Fact]
    public async Task NullsNotDistinct_OnSupportedTarget_BuildsWithoutWarning()
    {
        const string tableSql =
            "CREATE TABLE tv_supported (id integer PRIMARY KEY, email text);";
        const string indexSql =
            "CREATE UNIQUE INDEX ix_tv_supported_email ON tv_supported (email) NULLS NOT DISTINCT;";

        var result = await BuildAsync(
            new Postgresql15DatabaseSchemaProvider(),
            ("Supported.sql", tableSql),
            ("Index.sql", indexSql));

        Assert.Empty(result.Warnings);

        var index = Assert.Single(
            result.Model.Elements, e => e.Type == PostgresElementTypes.SqlIndex);

        Assert.Contains(index.Properties,
            p => p.Name == PostgresPropertyNames.NullsNotDistinct && Equals(p.Value, true));
    }
}
