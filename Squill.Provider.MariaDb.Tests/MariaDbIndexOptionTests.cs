using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Index options declared on a CREATE INDEX or an inline KEY (issue #211). The whole
/// <c>indexOption</c> list used to be walked solely to recover a trailing <c>USING</c>, so an
/// index declaring <c>COMMENT</c>, <c>INVISIBLE</c> or <c>WITH PARSER</c> built, deployed and
/// compared as if none of it were written.
///
/// Which options are modeled and which only warn is measured against live servers rather than
/// read off the grammar, which accepts far more than either engine round-trips. The visibility
/// keyword is the sharpest case: the two engines spell it differently and each rejects the
/// other's spelling with a syntax error, so it is resolved through a capability rather than
/// written the same way for both.
/// </summary>
public class MariaDbIndexOptionTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(string sql,
        MariaDbFamilyDatabaseSchemaProvider? engine = null)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Index.sql", FileKind.Compile, sql));

        return new ParserWorkspaceModelBuilder(
            workspace, new AntlrMariaDbParser(), engine ?? new MariaDb12DatabaseSchemaProvider());
    }

    private const string Table = "CREATE TABLE t (a int, body text);\n";

    private static async Task<(Element Index, IReadOnlyList<SqlSourceDiagnostic> Warnings)> BuildIndexAsync(
        string indexSql, MariaDbFamilyDatabaseSchemaProvider? engine = null)
    {
        var result = await BuilderFor(Table + indexSql, engine)
            .ExtractModelAsync(TestContext.Current.CancellationToken);
        var index = result.Model.Elements.Single(e => e.Type == MariaDbElementTypes.SqlIndex);

        return (index, result.Warnings);
    }

    [Fact]
    public async Task IndexComment_IsModeled()
    {
        var (index, warnings) = await BuildIndexAsync(
            "CREATE INDEX ix ON t (a) COMMENT 'why this exists';");

        Assert.Equal("why this exists", index.GetProperty<string>(MariaDbPropertyNames.Comment));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// The load-bearing half of the omit-when-default convention: both engines report
    /// INDEX_COMMENT as the empty string rather than NULL for an index that declared none, so an
    /// index without a comment must carry no property at all.
    /// </summary>
    [Fact]
    public async Task NoComment_StoresNoCommentProperty()
    {
        var (index, _) = await BuildIndexAsync("CREATE INDEX ix ON t (a);");

        Assert.Null(index.GetProperty<string>(MariaDbPropertyNames.Comment));
    }

    [Fact]
    public async Task Ignored_IsModeledOnMariaDb()
    {
        var (index, warnings) = await BuildIndexAsync(
            "CREATE INDEX ix ON t (a) IGNORED;", new MariaDb12DatabaseSchemaProvider());

        Assert.True(index.GetProperty<bool?>(MariaDbPropertyNames.IsHiddenFromOptimizer));
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Invisible_IsModeledOnMySql()
    {
        var (index, warnings) = await BuildIndexAsync(
            "CREATE INDEX ix ON t (a) INVISIBLE;", new MySql9DatabaseSchemaProvider());

        Assert.True(index.GetProperty<bool?>(MariaDbPropertyNames.IsHiddenFromOptimizer));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Measured, MariaDB rejects INVISIBLE with a syntax error, so a build targeting MariaDB must
    /// not model it: the source would never have reached the server to begin with.
    /// </summary>
    [Fact]
    public async Task ForeignVisibilityKeyword_IsNotModeled()
    {
        var (mariaDb, _) = await BuildIndexAsync(
            "CREATE INDEX ix ON t (a) INVISIBLE;", new MariaDb12DatabaseSchemaProvider());
        var (mySql, _) = await BuildIndexAsync(
            "CREATE INDEX ix ON t (a) IGNORED;", new MySql9DatabaseSchemaProvider());

        Assert.Null(mariaDb.GetProperty<bool?>(MariaDbPropertyNames.IsHiddenFromOptimizer));
        Assert.Null(mySql.GetProperty<bool?>(MariaDbPropertyNames.IsHiddenFromOptimizer));
    }

    [Fact]
    public async Task VisibleIndex_StoresNoVisibilityProperty()
    {
        var (index, _) = await BuildIndexAsync("CREATE INDEX ix ON t (a);");

        Assert.Null(index.GetProperty<bool?>(MariaDbPropertyNames.IsHiddenFromOptimizer));
    }

    /// <summary>
    /// Measured: MySQL honours WITH PARSER and echoes it from SHOW CREATE TABLE, but reports it
    /// in no readable catalog view, so the extract side cannot see it. A modeled value would
    /// re-diff on every deploy, so it warns instead.
    /// </summary>
    [Fact]
    public async Task WithParser_WarnsAndIsNotModeled()
    {
        var (index, warnings) = await BuildIndexAsync(
            "CREATE FULLTEXT INDEX ix ON t (body) WITH PARSER ngram;");

        Assert.DoesNotContain(index.Properties,
            p => p.Name.Contains("Parser", StringComparison.Ordinal));

        var warning = Assert.Single(warnings);
        Assert.Contains("WITH PARSER", warning.Message, StringComparison.Ordinal);
        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
    }

    /// <summary>
    /// Measured: MySQL's InnoDB accepts KEY_BLOCK_SIZE and silently discards it, so it does not
    /// survive even into SHOW CREATE TABLE there.
    /// </summary>
    [Fact]
    public async Task KeyBlockSize_WarnsAndIsNotModeled()
    {
        var (index, warnings) = await BuildIndexAsync("CREATE INDEX ix ON t (a) KEY_BLOCK_SIZE=8;");

        Assert.DoesNotContain(index.Properties,
            p => p.Name.Contains("KeyBlockSize", StringComparison.Ordinal));

        var warning = Assert.Single(warnings);
        Assert.Contains("KEY_BLOCK_SIZE", warning.Message, StringComparison.Ordinal);
        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
    }

    /// <summary>
    /// Changing a comment must change the hash, or editing one would deploy as a no-op.
    /// </summary>
    [Fact]
    public async Task ChangingTheComment_ChangesTheHash()
    {
        var (a, _) = await BuildIndexAsync("CREATE INDEX ix ON t (a) COMMENT 'one';");
        var (b, _) = await BuildIndexAsync("CREATE INDEX ix ON t (a) COMMENT 'two';");

        Assert.False(HashUtility.HashesEqual(a.Hash, b.Hash));
    }

    private static async Task<string> ScriptAsync(
        string indexSql, MariaDbFamilyDatabaseSchemaProvider engine)
    {
        var result = await BuilderFor(Table + indexSql, engine)
            .ExtractModelAsync(TestContext.Current.CancellationToken);
        var comparison = SchemaCompare.Compare(
            new MariaDbDatabaseProvider("Server=unused"), result.Model, new Model());

        return new MariaDbScriptGenerator(engine).GenerateScript(comparison);
    }

    [Fact]
    public async Task Comment_IsScripted()
    {
        var script = await ScriptAsync(
            "CREATE INDEX ix ON t (a) COMMENT 'note';", new MariaDb12DatabaseSchemaProvider());

        Assert.Contains("COMMENT 'note'", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// An apostrophe in a comment has to be doubled, or the generated DDL ends the literal early
    /// and fails to parse.
    /// </summary>
    [Fact]
    public async Task CommentWithApostrophe_IsEscaped()
    {
        var script = await ScriptAsync(
            "CREATE INDEX ix ON t (a) COMMENT 'it''s here';",
            new MariaDb12DatabaseSchemaProvider());

        Assert.Contains("COMMENT 'it''s here'", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The engine-specific half: the same model must script IGNORED for MariaDB and INVISIBLE
    /// for MySQL, because each engine rejects the other's keyword outright.
    /// </summary>
    [Fact]
    public async Task HiddenIndex_ScriptsIgnoredOnMariaDb()
    {
        var script = await ScriptAsync(
            "CREATE INDEX ix ON t (a) IGNORED;", new MariaDb12DatabaseSchemaProvider());

        Assert.Contains(" IGNORED", script, StringComparison.Ordinal);
        Assert.DoesNotContain("INVISIBLE", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HiddenIndex_ScriptsInvisibleOnMySql()
    {
        var script = await ScriptAsync(
            "CREATE INDEX ix ON t (a) INVISIBLE;", new MySql9DatabaseSchemaProvider());

        Assert.Contains(" INVISIBLE", script, StringComparison.Ordinal);
        Assert.DoesNotContain("IGNORED", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainIndex_ScriptsNoOptions()
    {
        var script = await ScriptAsync(
            "CREATE INDEX ix ON t (a);", new MariaDb12DatabaseSchemaProvider());

        Assert.DoesNotContain("COMMENT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("IGNORED", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// A unique index is written into the table body as a UNIQUE KEY rather than as a CREATE
    /// INDEX, so it needs its own rendering of the same options.
    /// </summary>
    [Fact]
    public async Task UniqueIndexOptions_AreScriptedInTheTableBody()
    {
        var result = await BuilderFor(
                "CREATE TABLE t (a int, UNIQUE KEY ix (a) COMMENT 'uq');",
                new MariaDb12DatabaseSchemaProvider())
            .ExtractModelAsync(TestContext.Current.CancellationToken);
        var comparison = SchemaCompare.Compare(
            new MariaDbDatabaseProvider("Server=unused"), result.Model, new Model());
        var script = new MariaDbScriptGenerator(new MariaDb12DatabaseSchemaProvider())
            .GenerateScript(comparison);

        Assert.Contains("UNIQUE KEY `ix` (`a`) COMMENT 'uq'", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// An index declared inline in a CREATE TABLE reaches the same option list as a standalone
    /// CREATE INDEX, so it must warn for the unmodelable ones too rather than dropping them
    /// silently on that path alone.
    /// </summary>
    [Fact]
    public async Task InlineIndexOptions_AreModeledAndWarnedLikeStandaloneOnes()
    {
        var result = await BuilderFor(
                "CREATE TABLE t (a int, body text, KEY ix (a) COMMENT 'inline' KEY_BLOCK_SIZE=8);",
                new MariaDb12DatabaseSchemaProvider())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("KEY_BLOCK_SIZE", warning.Message, StringComparison.Ordinal);
        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
    }

    [Fact]
    public async Task HidingAnIndex_ChangesTheHash()
    {
        var (visible, _) = await BuildIndexAsync("CREATE INDEX ix ON t (a);");
        var (hidden, _) = await BuildIndexAsync("CREATE INDEX ix ON t (a) IGNORED;");

        Assert.False(HashUtility.HashesEqual(visible.Hash, hidden.Hash));
    }
}
