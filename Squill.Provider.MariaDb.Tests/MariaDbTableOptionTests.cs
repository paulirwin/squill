using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Table options declared on a CREATE TABLE (issue #207). The whole <c>tableOption</c> clause
/// used to be discarded with no diagnostic on both sides of the round trip, so a table declaring
/// <c>ENGINE=MyISAM DEFAULT CHARSET=utf8mb4 COMMENT='audit log'</c> built, deployed and compared
/// as if none of it were written.
///
/// Which options are modeled and which only warn is measured against live servers rather than
/// read off the grammar, which accepts far more than either engine round-trips. See
/// <see cref="MariaDbPropertyNames.Engine"/> for the per-option rationale.
/// </summary>
public class MariaDbTableOptionTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(string sql,
        MariaDbFamilyDatabaseSchemaProvider? engine = null)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Table.sql", FileKind.Compile, sql));

        return new ParserWorkspaceModelBuilder(
            workspace, new AntlrMariaDbParser(), engine ?? new MariaDb12DatabaseSchemaProvider());
    }

    private static async Task<(Element Table, IReadOnlyList<SqlSourceDiagnostic> Warnings)> BuildTableAsync(
        string sql, MariaDbFamilyDatabaseSchemaProvider? engine = null)
    {
        var result = await BuilderFor(sql, engine).ExtractModelAsync(TestContext.Current.CancellationToken);
        var table = result.Model.Elements.Single(e => e.Type == MariaDbElementTypes.SqlTable);

        return (table, result.Warnings);
    }

    [Fact]
    public async Task Engine_IsModeled()
    {
        var (table, warnings) = await BuildTableAsync("CREATE TABLE t (a int) ENGINE=MyISAM;");

        Assert.Equal("myisam", table.GetProperty<string>(MariaDbPropertyNames.Engine));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Engine names are case-folded, because the catalog's casing follows no rule the parse side
    /// could reproduce. Measured: both engines accept any spelling on input and report back their
    /// own, and the two disagree about what that is (MariaDB 12 reports <c>MRG_MyISAM</c> where
    /// MySQL 9 reports <c>MRG_MYISAM</c>, and MySQL reports a lower-case <c>ndbcluster</c>).
    /// Folding both sides is what lets a declared engine hash-match an extracted one without a
    /// hardcoded table of names that would be wrong on one engine or the other.
    /// </summary>
    [Theory]
    [InlineData("myisam")]
    [InlineData("MyISAM")]
    [InlineData("MYISAM")]
    public async Task Engine_SpellingIsCanonicalized(string spelling)
    {
        var (table, _) = await BuildTableAsync($"CREATE TABLE t (a int) ENGINE={spelling};");

        Assert.Equal("myisam", table.GetProperty<string>(MariaDbPropertyNames.Engine));
    }

    /// <summary>
    /// Declaring the engine a table would get anyway records nothing, so it leaves the same mark
    /// as declaring no ENGINE at all. The extractor cannot tell the two apart — the catalog names
    /// an engine for every table — so recording it here would make a table that spells out its
    /// default engine re-diff against its own database forever.
    /// </summary>
    [Theory]
    [InlineData("InnoDB")]
    [InlineData("innodb")]
    public async Task Engine_NamingTheDefault_RecordsNothing(string spelling)
    {
        var (table, warnings) = await BuildTableAsync($"CREATE TABLE t (a int) ENGINE={spelling};");

        Assert.Null(table.GetProperty<string>(MariaDbPropertyNames.Engine));
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Comment_IsModeled()
    {
        var (table, warnings) = await BuildTableAsync("CREATE TABLE t (a int) COMMENT='audit log';");

        Assert.Equal("audit log", table.GetProperty<string>(MariaDbPropertyNames.TableComment));
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Collate_IsModeled()
    {
        var (table, warnings) = await BuildTableAsync(
            "CREATE TABLE t (a int) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;");

        Assert.Equal("utf8mb4_bin", table.GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A CHARSET with no COLLATE resolves to that charset's default collation, and the catalog
    /// reports only the resolved collation. Resolving it is a server fact the build has no
    /// server to ask, and the answer differs between the engines: measured, <c>utf8mb4</c>
    /// defaults to <c>utf8mb4_uca1400_ai_ci</c> on MariaDB 12 and <c>utf8mb4_0900_ai_ci</c> on
    /// MySQL 9. Guessing either would model a collation the other engine never reports back, so
    /// a bare CHARSET warns instead.
    /// </summary>
    [Fact]
    public async Task Charset_WithoutCollate_WarnsAndIsNotModeled()
    {
        var (table, warnings) = await BuildTableAsync("CREATE TABLE t (a int) DEFAULT CHARSET=latin1;");

        Assert.Null(table.GetProperty<string>(MariaDbPropertyNames.Collation));

        var warning = Assert.Single(warnings);
        Assert.Contains("CHARSET", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A CHARSET written alongside an explicit COLLATE is redundant rather than unmodeled: the
    /// collation it would have resolved to is stated outright, so the pair round-trips and
    /// nothing warns.
    /// </summary>
    [Fact]
    public async Task Charset_WithExplicitCollate_DoesNotWarn()
    {
        var (table, warnings) = await BuildTableAsync(
            "CREATE TABLE t (a int) DEFAULT CHARSET=latin1 COLLATE=latin1_bin;");

        Assert.Equal("latin1_bin", table.GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A table that declares nothing must record nothing, so it hash-matches one extracted from
    /// either engine. The default collation genuinely differs between them (measured:
    /// <c>utf8mb4_uca1400_ai_ci</c> on MariaDB 12, <c>utf8mb4_0900_ai_ci</c> on MySQL 9), so
    /// resolving an absent clause to a default would make the same source build to two
    /// different models.
    /// </summary>
    [Fact]
    public async Task NoOptions_RecordsNothing()
    {
        var (table, warnings) = await BuildTableAsync("CREATE TABLE t (a int);");

        Assert.Null(table.GetProperty<string>(MariaDbPropertyNames.Engine));
        Assert.Null(table.GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Null(table.GetProperty<string>(MariaDbPropertyNames.TableComment));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// AUTO_INCREMENT is a live counter, not a schema facet: measured, a table declared
    /// <c>AUTO_INCREMENT=100</c> reports 103 after three inserts. Modeling it would re-diff
    /// against any table that has ever been written to, so it warns and stays out of the model.
    /// </summary>
    [Fact]
    public async Task AutoIncrementSeed_WarnsAndIsNotModeled()
    {
        var (table, warnings) = await BuildTableAsync(
            "CREATE TABLE t (a int NOT NULL AUTO_INCREMENT PRIMARY KEY) AUTO_INCREMENT=100;");

        Assert.DoesNotContain(table.Properties, p => p.Name.Contains("AutoIncrementSeed", StringComparison.Ordinal));

        var warning = Assert.Single(warnings);
        Assert.Contains("AUTO_INCREMENT", warning.Message, StringComparison.Ordinal);
        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
    }

    /// <summary>
    /// ROW_FORMAT and the rest of the clause persist, but the catalog reports a value for a
    /// table that never declared one (measured: <c>Dynamic</c> on a bare CREATE TABLE), so a
    /// declared default is indistinguishable from an absent clause and cannot round-trip yet.
    /// </summary>
    [Theory]
    [InlineData("ROW_FORMAT=COMPRESSED", "ROW_FORMAT")]
    [InlineData("KEY_BLOCK_SIZE=8", "KEY_BLOCK_SIZE")]
    [InlineData("MAX_ROWS=1000", "MAX_ROWS")]
    public async Task OtherOptions_Warn(string option, string expected)
    {
        var (_, warnings) = await BuildTableAsync($"CREATE TABLE t (a int) {option};");

        var warning = Assert.Single(warnings);
        Assert.Contains(expected, warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
    }

    /// <summary>
    /// The warning points at the option itself rather than at the statement it belongs to, so a
    /// table with a long column list sends the reader to the line they have to edit.
    /// </summary>
    [Fact]
    public async Task Warning_AnchorsToTheOption()
    {
        var (_, warnings) = await BuildTableAsync("""
            CREATE TABLE t
            (
                a int
            ) ROW_FORMAT=COMPRESSED;
            """);

        var warning = Assert.Single(warnings);
        Assert.Equal("Table.sql", warning.SourceFile);
        Assert.Equal(4, warning.Line);
    }

    /// <summary>
    /// Options may be written comma-separated, and each is decided on its own merits: the
    /// modeled ones are recorded and only the unmodeled one warns.
    /// </summary>
    [Fact]
    public async Task MixedOptions_ModelTheKnownAndWarnTheRest()
    {
        var (table, warnings) = await BuildTableAsync(
            "CREATE TABLE t (a int) ENGINE=MyISAM, COMMENT='x', ROW_FORMAT=COMPRESSED;");

        Assert.Equal("myisam", table.GetProperty<string>(MariaDbPropertyNames.Engine));
        Assert.Equal("x", table.GetProperty<string>(MariaDbPropertyNames.TableComment));

        var warning = Assert.Single(warnings);
        Assert.Contains("ROW_FORMAT", warning.Message, StringComparison.OrdinalIgnoreCase);
    }
}
