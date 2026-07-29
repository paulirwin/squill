using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Issue #158: two ways the generated-column path produced SQL Postgres could not run.
///
/// Adding or dropping a column's generated-ness left only one side carrying a generation
/// expression, which <see cref="PostgresTableDiffAnalyzer"/> did not count as a difference
/// needing a rebuild, so the change fell through to the ALTER path — whose clauses cover only
/// type, nullability, and default. That emitted an empty command string, which the deployer
/// then handed to Npgsql.
///
/// Separately, dropping a generated column alongside the columns its expression reads emitted
/// the per-column DROPs in diff order, so an input column could be dropped while the generated
/// column still depended on it (SQLSTATE 2BP01).
/// </summary>
public class PostgresGeneratedColumnAlterTests
{
    private static async Task<SchemaComparison> CompareAsync(string sourceSql, string targetSql)
    {
        var provider = new PostgresDatabaseProvider("Host=unused");

        var source = await BuildModelAsync(sourceSql);
        var target = await BuildModelAsync(targetSql);

        var options = new DeployOptions
        {
            AllowTableRebuild = true,
            BlockOnPossibleDataLoss = false,
        };

        return SchemaCompare.Compare(provider, source, target, options);
    }

    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()));

    private const string Generated = """
CREATE TABLE tally
(
    id  integer PRIMARY KEY,
    x   integer NOT NULL,
    y   integer NOT NULL,
    sum integer GENERATED ALWAYS AS (x + y) STORED
);
""";

    private const string Plain = """
CREATE TABLE tally
(
    id  integer PRIMARY KEY,
    x   integer NOT NULL,
    y   integer NOT NULL,
    sum integer
);
""";

    /// <summary>
    /// Dropping generated-ness must take the rebuild path. The ALTER path cannot express it,
    /// and producing an AlterDelta with no clauses is what made the deployer execute "".
    /// </summary>
    [Fact]
    public async Task DroppingGeneratedNess_RebuildsTheTable()
    {
        var comparison = await CompareAsync(Plain, Generated);

        var delta = Assert.Single(comparison.Deltas);
        Assert.IsType<RebuildTableDelta>(delta);
    }

    /// <summary>
    /// The reverse direction — making an ordinary column generated — is equally inexpressible
    /// by the ALTER path, and must rebuild rather than silently drop the expression.
    /// </summary>
    [Fact]
    public async Task AddingGeneratedNess_RebuildsTheTable()
    {
        var comparison = await CompareAsync(Generated, Plain);

        var delta = Assert.Single(comparison.Deltas);
        Assert.IsType<RebuildTableDelta>(delta);
    }

    /// <summary>
    /// The end result that matters: whatever delta is chosen, the script must not be empty, and
    /// the rebuilt table must declare the column without a generation expression.
    /// </summary>
    [Fact]
    public async Task DroppingGeneratedNess_ProducesANonEmptyScript()
    {
        var comparison = await CompareAsync(Plain, Generated);
        var script = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.DoesNotContain("GENERATED ALWAYS AS", script);
    }

    /// <summary>
    /// Every delta a comparison produces must render to something runnable. An empty per-delta
    /// script is what reached Npgsql as an uninitialized CommandText.
    /// </summary>
    [Fact]
    public async Task EveryDelta_ForAGeneratedNessChange_RendersNonEmptySql()
    {
        var comparison = await CompareAsync(Plain, Generated);
        var generator = new PostgresScriptGenerator();

        foreach (var delta in comparison.Deltas)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(generator.GenerateScriptForDelta(delta)),
                $"Delta {delta.GetType().Name} rendered no SQL.");
        }
    }

    /// <summary>
    /// Dropping a generated column together with the columns its expression reads must emit the
    /// generated column's DROP first: Postgres refuses to drop a column another column's
    /// generation expression depends on.
    /// </summary>
    [Fact]
    public async Task DroppingGeneratedColumnWithItsInputs_DropsTheGeneratedColumnFirst()
    {
        const string before = """
CREATE TABLE people
(
    id  integer PRIMARY KEY,
    x   integer NULL,
    y   integer NULL,
    sum integer GENERATED ALWAYS AS (x + y) STORED
);
""";
        const string after = """
CREATE TABLE people
(
    id integer PRIMARY KEY
);
""";

        var comparison = await CompareAsync(after, before);
        var script = new PostgresScriptGenerator().GenerateScript(comparison);

        // Whether this is applied by ALTERs or a rebuild, the generated column must never be
        // left depending on a column that was already dropped.
        if (script.Contains("DROP COLUMN"))
        {
            var sumAt = script.IndexOf("DROP COLUMN \"sum\"", StringComparison.Ordinal);
            var xAt = script.IndexOf("DROP COLUMN \"x\"", StringComparison.Ordinal);
            var yAt = script.IndexOf("DROP COLUMN \"y\"", StringComparison.Ordinal);

            Assert.True(sumAt >= 0, "The generated column must be dropped.");
            Assert.True(xAt < 0 || sumAt < xAt, "'sum' must be dropped before its input 'x'.");
            Assert.True(yAt < 0 || sumAt < yAt, "'sum' must be dropped before its input 'y'.");
        }

        Assert.False(string.IsNullOrWhiteSpace(script));
    }
}
