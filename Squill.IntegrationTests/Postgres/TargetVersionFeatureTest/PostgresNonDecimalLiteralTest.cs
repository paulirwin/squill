using Npgsql;
using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.TargetVersionFeatureTest;

/// <summary>
/// Confirms the PostgreSQL 16 boundary for non-decimal integer literals against a real server
/// (issue #191), the same way the <c>NULLS NOT DISTINCT</c> entry was confirmed for 15.
///
/// <para>
/// The server is pinned to 15 rather than 14, and the choice is the interesting part. 14 does not
/// have a concept of a malformed numeric literal here at all: it lexes <c>0x19</c> as the integer
/// <c>0</c> followed by the identifier <c>x19</c>, so <c>SELECT 0x1f</c> quietly returns <c>0</c>
/// there. 15 added the "trailing junk after numeric literal" rejection, and 16 added the literals
/// themselves. 15 is therefore the version that draws the boundary unambiguously — it rejects the
/// exact spelling that 16 accepts, which is what makes the catalogue's claim of "16 or later"
/// testable rather than merely documented.
/// </para>
/// </summary>
public class PostgresNonDecimalLiteralTest : PostgresIntegrationTestBase
{
    // The last major that rejects a non-decimal literal outright.
    protected override string DockerImageName => "postgres:15";

    private static async Task<BuildResult> BuildAsync(
        PostgresqlDatabaseSchemaProvider schemaProvider, string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Flags.sql", FileKind.Compile, sql));

        return await DacpacBuilder.BuildModelAsync(
            workspace, schemaProvider, TestContext.Current.CancellationToken);
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
    /// The build warns, and the server it warned about really does refuse the same DDL. Without
    /// the second half this would only prove Squill agrees with itself.
    /// </summary>
    [Theory]
    [InlineData("0x19", 'x')]
    [InlineData("0o17", 'o')]
    [InlineData("0b101", 'b')]
    public async Task NonDecimalLiteral_WarnsAtBuildAndIsRejectedByPostgres15(
        string literal, char suffix)
    {
        var sql = $"CREATE TABLE tv_flags_{suffix} "
                  + $"(id integer PRIMARY KEY, mask integer DEFAULT {literal});";

        var result = await BuildAsync(new Postgresql15DatabaseSchemaProvider(), sql);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Contains(literal, warning.Message);
        Assert.Contains("16", warning.Message);

        var ex = await ExecuteExpectingFailureAsync(sql);

        // 42601 syntax_error: "trailing junk after numeric literal".
        Assert.Equal("42601", ex.SqlState);
    }

    /// <summary>
    /// The generated-column case, which is where the warning does the most work. A DEFAULT is
    /// canonicalized to the value the engine stores before it is ever deployed, so a non-decimal
    /// literal in one is harmless by the time it reaches a server. A generation expression is
    /// rendered back out with the literal's source spelling, so the <c>CREATE TABLE</c> Squill
    /// scripts really does carry <c>0x19</c> — and this confirms that statement is refused by 15.
    /// </summary>
    [Fact]
    public async Task NonDecimalLiteral_InGeneratedColumn_WarnsAndIsRejectedByPostgres15()
    {
        const string sql = """
CREATE TABLE tv_gen
(
    id integer PRIMARY KEY,
    mask integer,
    scaled integer GENERATED ALWAYS AS (mask + 0x19) STORED
);
""";

        var result = await BuildAsync(new Postgresql15DatabaseSchemaProvider(), sql);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Contains("0x19", warning.Message);

        var ex = await ExecuteExpectingFailureAsync(sql);

        Assert.Equal("42601", ex.SqlState);
    }

    /// <summary>
    /// A target of 16 does not warn, and the model carries the literal's DECIMAL value. That is
    /// not an arbitrary choice: PostgreSQL normalizes the radix away when it stores the default
    /// (measured on 16, <c>DEFAULT 0x19</c> reads back as <c>25</c>), so modeling the source
    /// spelling would make every deploy see a phantom change. The decimal equivalent is executed
    /// here to show the value the two sides have to agree on.
    /// </summary>
    [Fact]
    public async Task NonDecimalLiteral_IsModeledAsTheValuePostgresStores()
    {
        var result = await BuildAsync(
            new Postgresql16DatabaseSchemaProvider(),
            "CREATE TABLE tv_norm (id integer PRIMARY KEY, mask integer DEFAULT 0x19);");

        Assert.Empty(result.Warnings);

        var table = Assert.Single(
            result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var column = table.Relationships
            .Single(r => r.Name == PostgresRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == "mask");

        Assert.Equal("25", column.GetProperty<string>(PostgresPropertyNames.DefaultValue));

        // The same value written the way this server accepts it stores as the same token the
        // model carries, so a redeploy against it produces no delta.
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var create = new NpgsqlCommand(
                         "CREATE TABLE tv_norm (id integer PRIMARY KEY, mask integer DEFAULT 25);",
                         connection))
        {
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var read = new NpgsqlCommand(
            "SELECT column_default FROM information_schema.columns "
            + "WHERE table_name = 'tv_norm' AND column_name = 'mask';",
            connection);

        var stored = (string?)await read.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal("25", stored);
    }
}
