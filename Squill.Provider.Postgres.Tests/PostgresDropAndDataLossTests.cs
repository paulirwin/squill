using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over DROP support (SSDT's DropObjectsNotInSource) and the
/// block-on-possible-data-loss option (SSDT's BlockOnPossibleDataLoss), issues #36/#31.
/// A "source" (desired) and "target" (current) schema are parsed into models, diffed with
/// explicit options, and the resulting deltas and generated SQL are asserted.
/// </summary>
public class PostgresDropAndDataLossTests
{
    private static async Task<SchemaComparison> CompareAsync(
        string sourceSql, string targetSql, DeployOptions options)
    {
        var provider = new PostgresDatabaseProvider("Host=unused");
        var source = await BuildModelAsync(sourceSql);
        var target = await BuildModelAsync(targetSql);

        return SchemaCompare.Compare(provider, source, target, options);
    }

    private static async Task<Model> BuildModelAsync(string sql)
    {
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(workspace, parser).ExtractModelAsync()).Model;
    }

    // Options that keep the focus on drops: object drops on, data-loss block off so the
    // drop produces a delta rather than throwing.
    private static DeployOptions DropUnblocked => new()
    {
        DropObjectsNotInSource = true,
        BlockOnPossibleDataLoss = false,
    };

    [Fact]
    public async Task ExtraTableInTarget_NotDropped_ByDefault()
    {
        // The default (DropObjectsNotInSource = false) must NOT drop a table that exists
        // in the database but not the DACPAC — dropping objects requires opting in.
        const string source = """
CREATE TABLE keep (id integer PRIMARY KEY);
""";
        const string target = """
CREATE TABLE keep (id integer PRIMARY KEY);
CREATE TABLE gone (id integer PRIMARY KEY);
""";

        var comparison = await CompareAsync(source, target, DeployOptions.Default);

        Assert.Empty(comparison.Deltas);
    }

    [Fact]
    public async Task ExtraTableInTarget_Dropped_WhenOptedIn()
    {
        const string source = """
CREATE TABLE keep (id integer PRIMARY KEY);
""";
        const string target = """
CREATE TABLE keep (id integer PRIMARY KEY);
CREATE TABLE gone (id integer PRIMARY KEY);
""";

        var comparison = await CompareAsync(source, target, DropUnblocked);

        var drop = Assert.IsType<DropDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal("gone", drop.Element.Name);
        Assert.True(drop.CausesDataLoss, "Dropping a table loses its data.");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);
        Assert.Contains("DROP TABLE \"gone\" CASCADE;", sql);
    }

    [Fact]
    public async Task ExtraExtensionInTarget_Dropped_WhenOptedIn_NoDataLoss()
    {
        const string source = "CREATE EXTENSION citext;";
        const string target = """
CREATE EXTENSION citext;
CREATE EXTENSION hstore;
""";

        var comparison = await CompareAsync(source, target, DropUnblocked);

        var drop = Assert.IsType<DropDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal("hstore", drop.Element.Name);
        Assert.False(drop.CausesDataLoss, "Dropping an extension loses no table data.");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);
        Assert.Contains("DROP EXTENSION IF EXISTS \"hstore\";", sql);
    }

    [Fact]
    public async Task ExtraIndexInTarget_Dropped_WhenOptedIn()
    {
        const string source = """
CREATE TABLE film (film_id integer PRIMARY KEY, title varchar(255));
""";
        const string target = """
CREATE TABLE film (film_id integer PRIMARY KEY, title varchar(255));
CREATE INDEX idx_film_title ON film (title);
""";

        var comparison = await CompareAsync(source, target, DropUnblocked);

        var drop = Assert.IsType<DropDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(PostgresElementTypes.SqlIndex, drop.Element.Type);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);
        Assert.Contains("DROP INDEX IF EXISTS", sql);
        Assert.Contains("idx_film_title", sql);
    }

    [Fact]
    public async Task DroppingTable_IsBlocked_ByDefaultDataLossGuard()
    {
        const string source = """
CREATE TABLE keep (id integer PRIMARY KEY);
""";
        const string target = """
CREATE TABLE keep (id integer PRIMARY KEY);
CREATE TABLE gone (id integer PRIMARY KEY);
""";

        // Opt into dropping objects but leave the data-loss guard on (the default): the
        // table drop must be blocked.
        var options = new DeployOptions { DropObjectsNotInSource = true };

        var ex = await Assert.ThrowsAsync<PossibleDataLossException>(
            () => CompareAsync(source, target, options));

        Assert.Contains(ex.Reasons, r => r.Contains("gone"));
    }

    [Fact]
    public async Task DroppingColumn_IsBlocked_ByDefaultDataLossGuard()
    {
        // A column drop is part of a table ALTER (not gated by DropObjectsNotInSource) but
        // is still data-losing, so the default guard blocks it.
        const string target = """
CREATE TABLE film (film_id integer PRIMARY KEY, title varchar(255), notes text);
""";
        const string source = """
CREATE TABLE film (film_id integer PRIMARY KEY, title varchar(255));
""";

        var ex = await Assert.ThrowsAsync<PossibleDataLossException>(
            () => CompareAsync(source, target, DeployOptions.Default));

        Assert.Contains(ex.Reasons, r => r.Contains("notes"));
    }

    [Fact]
    public async Task DroppingColumn_Allowed_WhenDataLossPermitted()
    {
        const string target = """
CREATE TABLE film (film_id integer PRIMARY KEY, title varchar(255), notes text);
""";
        const string source = """
CREATE TABLE film (film_id integer PRIMARY KEY, title varchar(255));
""";

        var options = new DeployOptions { BlockOnPossibleDataLoss = false };
        var comparison = await CompareAsync(source, target, options);

        var alter = Assert.IsType<AlterDelta>(Assert.Single(comparison.Deltas));
        Assert.Contains(alter.ColumnChanges, c => c.Kind == ColumnChangeKind.Drop);
    }

    [Fact]
    public async Task LosslessRebuild_IsNotBlocked_ByDefaultDataLossGuard()
    {
        // Inserting a column mid-table forces a rebuild, but it copies every row
        // losslessly — no column is dropped — so the default guard must NOT block it.
        // (SSDT's block-on-possible-data-loss is about loss, not data movement.)
        const string target = """
CREATE TABLE film (film_id integer PRIMARY KEY, title varchar(255));
""";
        const string source = """
CREATE TABLE film (film_id integer PRIMARY KEY, extra text, title varchar(255));
""";

        var comparison = await CompareAsync(source, target, DeployOptions.Default);

        var rebuild = Assert.IsType<RebuildTableDelta>(Assert.Single(comparison.Deltas));
        Assert.False(rebuild.DropsData);
        Assert.False(comparison.CausesDataLoss);
    }

    [Fact]
    public async Task RebuildThatDropsColumn_IsBlocked_ByDefaultDataLossGuard()
    {
        // A rebuild that also drops a column (here: reorder + drop 'notes') destroys that
        // column's data, so the default guard blocks it.
        const string target = """
CREATE TABLE film (film_id integer PRIMARY KEY, title varchar(255), notes text);
""";
        const string source = """
CREATE TABLE film (film_id integer PRIMARY KEY, extra text, title varchar(255));
""";

        var ex = await Assert.ThrowsAsync<PossibleDataLossException>(
            () => CompareAsync(source, target, DeployOptions.Default));

        Assert.Contains(ex.Reasons, r => r.Contains("drops one or more columns"));
    }

    [Fact]
    public async Task AddingObjects_IsNotBlocked_ByDataLossGuard()
    {
        // Pure additions never lose data, so the default guard lets them through.
        const string source = """
CREATE TABLE film (film_id integer PRIMARY KEY);
CREATE TABLE actor (actor_id integer PRIMARY KEY);
""";
        const string target = """
CREATE TABLE film (film_id integer PRIMARY KEY);
""";

        var comparison = await CompareAsync(source, target, DeployOptions.Default);

        var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal("actor", create.Element.Name);
    }
}
