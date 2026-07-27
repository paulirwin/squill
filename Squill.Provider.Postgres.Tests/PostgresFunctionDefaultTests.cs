using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Tests modeling of non-constant column <c>DEFAULT</c>s (issue #124) — the function-call
/// defaults such as <c>now()</c> that Pagila's ubiquitous <c>last_update</c> columns use.
///
/// The load-bearing property is the round trip: a default parsed from source and the same
/// default read back out of <c>information_schema.columns</c> must canonicalize to the same
/// token, or every deploy would see a phantom column change. Postgres preserves the spelling
/// it was given (<c>now()</c> stays <c>now()</c>, <c>CURRENT_TIMESTAMP</c> stays
/// <c>CURRENT_TIMESTAMP</c>) while normalizing case, whitespace and the <c>pg_catalog.</c>
/// prefix, so each supported spelling maps to its own canonical token rather than being
/// folded together.
/// </summary>
public class PostgresFunctionDefaultTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));
        return new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());
    }

    private static async Task<string?> DefaultOfAsync(string columnSql)
    {
        var builder = BuilderFor($"CREATE TABLE t (id integer PRIMARY KEY, c {columnSql});");
        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var table = Assert.Single(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var column = table.Relationships
            .Single(r => r.Name == PostgresRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == "c");

        return column.GetProperty<string>(PostgresPropertyNames.DefaultValue);
    }

    [Theory]
    // The spelling written in source is preserved, normalized to lower case.
    [InlineData("timestamp DEFAULT now()", "now()")]
    [InlineData("timestamp DEFAULT NOW()", "now()")]
    [InlineData("timestamp DEFAULT Now()", "now()")]
    // Postgres resolves an explicit pg_catalog. prefix away when it stores the default.
    [InlineData("timestamp DEFAULT pg_catalog.now()", "now()")]
    [InlineData("uuid DEFAULT gen_random_uuid()", "gen_random_uuid()")]
    public async Task FunctionDefault_IsModeledWithCanonicalToken(string columnSql, string expected)
    {
        Assert.Equal(expected, await DefaultOfAsync(columnSql));
    }

    [Fact]
    public async Task FunctionDefault_NoLongerWarns()
    {
        var builder = BuilderFor("""
CREATE TABLE event
(
    id integer PRIMARY KEY,
    created_at timestamp DEFAULT now()
);
""");

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ConstantDefault_StillModeled()
    {
        Assert.Equal("0", await DefaultOfAsync("integer DEFAULT 0"));
        Assert.Equal("'active'", await DefaultOfAsync("varchar(20) DEFAULT 'active'"));
    }

    /// <summary>
    /// A serial column's default is a <c>nextval(...)</c> sequence call, which is already
    /// represented by the column's serial-ness. Modeling it as a default too would duplicate
    /// it and, because the sequence name is database-generated, would not round-trip.
    /// </summary>
    [Fact]
    public async Task SerialDefault_IsNotModeledAsADefault()
    {
        Assert.Null(await DefaultOfAsync("serial"));
    }

    /// <summary>
    /// An arbitrary function call is not on the allowlist: its stored form may be rewritten by
    /// Postgres (argument casts, schema resolution), so it cannot be guaranteed to round-trip
    /// and stays unmodeled with the SQ1002 warning.
    /// </summary>
    [Fact]
    public async Task UnknownFunctionDefault_StillWarnsAndIsUnmodeled()
    {
        var builder = BuilderFor("""
CREATE TABLE t
(
    id integer PRIMARY KEY,
    c  integer DEFAULT some_custom_fn(1)
);
""");

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("c", warning.Message);
    }
}
