using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Column attributes declared on a CREATE TABLE column (issue #216). All of them used to reach
/// <see cref="IgnoredColumnConstraint"/> and be reported by one generic warning naming
/// "(COMMENT, COLLATE, …)", which understated the loss: <c>SERIAL DEFAULT VALUE</c> is shorthand
/// for <c>NOT NULL AUTO_INCREMENT UNIQUE</c>, so dropping it lost a generated value and an index
/// rather than a cosmetic facet.
///
/// Which attributes are modeled and which only warn is measured against live servers rather than
/// read off the grammar, which accepts more than either engine does.
/// </summary>
public class MariaDbColumnAttributeTests
{
    private static async Task<(Model Model, IReadOnlyList<SqlSourceDiagnostic> Warnings)> BuildAsync(
        string sql, MariaDbFamilyDatabaseSchemaProvider? engine = null)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Table.sql", FileKind.Compile, sql));

        var result = await new ParserWorkspaceModelBuilder(
                workspace, new AntlrMariaDbParser(), engine ?? new MariaDb12DatabaseSchemaProvider())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        return (result.Model, result.Warnings);
    }

    private static Element SingleColumn(Model model, string columnName)
        => model.Elements
            .Single(e => e.Type == MariaDbElementTypes.SqlTable)
            .Relationships.Single(r => r.Name == MariaDbRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == columnName);

    // ---- COMMENT ----

    [Fact]
    public async Task Comment_IsModeled()
    {
        var (model, warnings) = await BuildAsync("CREATE TABLE t (c int COMMENT 'a note');");

        Assert.Equal("a note",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.ColumnComment));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// An empty COMMENT is what both engines report for a column that declared none, so writing
    /// one records nothing. Storing it would make the column re-diff against its own database.
    /// </summary>
    [Fact]
    public async Task Comment_Empty_RecordsNothing()
    {
        var (model, _) = await BuildAsync("CREATE TABLE t (c int COMMENT '');");

        Assert.Null(SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.ColumnComment));
    }

    // ---- COLLATE ----

    [Fact]
    public async Task Collation_IsModeled()
    {
        var (model, warnings) = await BuildAsync(
            "CREATE TABLE t (c varchar(50) COLLATE latin1_bin);");

        Assert.Equal("latin1_bin",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Both engines accept COLLATE before or after the nullability suffix (measured), and the
    /// grammar routes the two spellings to different places: a leading one is absorbed into
    /// <c>stringDataType</c>, a trailing one arrives as a column constraint. Both mean the same
    /// thing, so both must reach the model, or the deployed column silently gets the table's
    /// collation instead of the declared one.
    /// </summary>
    [Theory]
    [InlineData("varchar(50) COLLATE latin1_bin NOT NULL")]
    [InlineData("varchar(50) NOT NULL COLLATE latin1_bin")]
    public async Task Collation_IsModeled_InEitherPosition(string column)
    {
        var (model, warnings) = await BuildAsync($"CREATE TABLE t (c {column});");

        Assert.Equal("latin1_bin",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A column inherits its table's collation, not the engine's, so one that names the table's
    /// records nothing: the catalog reports the same value for it as for a column that declared
    /// none, and recording it would re-diff on every deploy.
    /// </summary>
    [Fact]
    public async Task Collation_NamingTheTableCollation_RecordsNothing()
    {
        var (model, warnings) = await BuildAsync(
            """
            CREATE TABLE t (c varchar(50) COLLATE latin1_general_ci)
            COLLATE=latin1_general_ci;
            """);

        Assert.Null(SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A column that differs from its table's collation still records one: this is the case the
    /// inheritance rule must not swallow.
    /// </summary>
    [Fact]
    public async Task Collation_DifferingFromTheTableCollation_IsModeled()
    {
        var (model, _) = await BuildAsync(
            """
            CREATE TABLE t (c varchar(50) COLLATE latin1_bin)
            COLLATE=latin1_general_ci;
            """);

        Assert.Equal("latin1_bin",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
    }

    /// <summary>
    /// Declaring the collation the column would get anyway records nothing, for the same reason
    /// the table-level COLLATE does (issue #207): every string column reports a COLLATION_NAME
    /// whether or not one was written, so the extractor cannot tell a declared default from an
    /// absent clause. Measured, the two engines even disagree on what that default is, which is
    /// why the answer comes from the schema provider rather than a constant.
    /// </summary>
    [Fact]
    public async Task Collation_NamingTheEngineDefault_RecordsNothing()
    {
        var engine = new MariaDb12DatabaseSchemaProvider();

        var (model, warnings) = await BuildAsync(
            $"CREATE TABLE t (c varchar(50) COLLATE {engine.DefaultCollation});", engine);

        Assert.Null(SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// The same source builds to different models on the two engines, because the collation that
    /// is "the default" differs between them. This is the pair that proves the previous test is
    /// reading the capability rather than a hardcoded name.
    /// </summary>
    [Fact]
    public async Task Collation_EngineDefault_IsPerEngine()
    {
        var mysql = new MySql9DatabaseSchemaProvider();
        var sql = $"CREATE TABLE t (c varchar(50) COLLATE {mysql.DefaultCollation});";

        var (onMySql, _) = await BuildAsync(sql, mysql);
        var (onMariaDb, _) = await BuildAsync(sql, new MariaDb12DatabaseSchemaProvider());

        Assert.Null(SingleColumn(onMySql, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Equal(mysql.DefaultCollation,
            SingleColumn(onMariaDb, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
    }

    // ---- INVISIBLE ----

    [Fact]
    public async Task Invisible_IsModeled()
    {
        var (model, warnings) = await BuildAsync("CREATE TABLE t (k int, c int INVISIBLE);");

        Assert.True(SingleColumn(model, "c").GetProperty<bool?>(MariaDbPropertyNames.IsInvisible));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A visible column is the default and records nothing, so it hash-matches an extracted
    /// column whose EXTRA is empty.
    /// </summary>
    [Fact]
    public async Task Visible_RecordsNothingAndDoesNotWarn()
    {
        var (model, warnings) = await BuildAsync(
            "CREATE TABLE t (k int, c int VISIBLE);", new MySql9DatabaseSchemaProvider());

        Assert.Null(SingleColumn(model, "c").GetProperty<bool?>(MariaDbPropertyNames.IsInvisible));
        Assert.Empty(warnings);
    }

    // ---- SERIAL DEFAULT VALUE ----

    /// <summary>
    /// Measured identically on MariaDB 12 and MySQL 9: the column becomes NOT NULL
    /// AUTO_INCREMENT and gains a unique index. All three have to reach the model, since each is
    /// separately observable in the deployed schema.
    /// </summary>
    [Fact]
    public async Task SerialDefaultValue_ExpandsToNotNullAutoIncrementAndUnique()
    {
        var (model, warnings) = await BuildAsync(
            "CREATE TABLE t (id bigint SERIAL DEFAULT VALUE, name varchar(50));");

        var column = SingleColumn(model, "id");

        Assert.True(column.GetProperty<bool?>(MariaDbPropertyNames.IsAutoIncrement));
        Assert.False(column.GetProperty<bool?>(MariaDbPropertyNames.IsNullable));

        Assert.Contains(model.Elements, e => e.Type == MariaDbElementTypes.SqlIndex);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// The index it creates is unique, not merely an index. A non-unique one would let duplicate
    /// values in, which is the constraint the shorthand exists to add.
    /// </summary>
    [Fact]
    public async Task SerialDefaultValue_IndexIsUnique()
    {
        var (model, _) = await BuildAsync("CREATE TABLE t (id bigint SERIAL DEFAULT VALUE);");

        var index = Assert.Single(model.Elements, e => e.Type == MariaDbElementTypes.SqlIndex);

        Assert.True(index.GetProperty<bool?>(MariaDbPropertyNames.IsUnique));
    }

    /// <summary>
    /// It is a UNIQUE index and not a primary key. Both engines report the column's COLUMN_KEY as
    /// 'PRI' (measured), because they report the first NOT NULL unique index that way, but
    /// SHOW CREATE TABLE proves it is a UNIQUE KEY. Modeling a primary key here would deploy a
    /// constraint the source never asked for.
    /// </summary>
    [Fact]
    public async Task SerialDefaultValue_IsNotAPrimaryKey()
    {
        var (model, _) = await BuildAsync("CREATE TABLE t (id bigint SERIAL DEFAULT VALUE);");

        Assert.DoesNotContain(model.Elements, e => e.Type == MariaDbElementTypes.SqlPrimaryKeyConstraint);
    }

    // ---- Attributes that cannot round-trip ----

    /// <summary>
    /// COLUMN_FORMAT and STORAGE are accepted by MySQL and rejected outright by MariaDB, and
    /// neither is reported by information_schema on either engine: MySQL preserves them only
    /// inside a SHOW CREATE TABLE version comment. A facet the extractor cannot see would
    /// re-diff on every deploy, so they warn instead of being modeled.
    /// </summary>
    [Theory]
    [InlineData("COLUMN_FORMAT DYNAMIC", "COLUMN_FORMAT")]
    [InlineData("STORAGE DISK", "STORAGE")]
    public async Task UnmodelableAttribute_Warns(string attribute, string expected)
    {
        var (_, warnings) = await BuildAsync($"CREATE TABLE t (k int, c int {attribute});");

        var warning = Assert.Single(warnings);
        Assert.Contains(expected, warning.Message, StringComparison.Ordinal);
        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
    }

    /// <summary>
    /// The warning names the attribute that was dropped. The old wording listed
    /// "(COMMENT, COLLATE, …)" for every attribute alike, which pointed the reader at clauses
    /// they had not written.
    /// </summary>
    [Fact]
    public async Task UnmodelableAttribute_WarningNamesTheColumnAndAttribute()
    {
        var (_, warnings) = await BuildAsync("CREATE TABLE t (k int, c int STORAGE DISK);");

        var warning = Assert.Single(warnings);

        Assert.Contains("t.c", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("COMMENT", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A modeled attribute alongside an unmodelable one: each is decided on its own merits, so
    /// only the one that cannot round-trip warns.
    /// </summary>
    [Fact]
    public async Task MixedAttributes_ModelTheKnownAndWarnTheRest()
    {
        var (model, warnings) = await BuildAsync(
            "CREATE TABLE t (k int, c int COMMENT 'kept' STORAGE DISK);");

        Assert.Equal("kept",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.ColumnComment));

        var warning = Assert.Single(warnings);
        Assert.Contains("STORAGE", warning.Message, StringComparison.Ordinal);
    }
}
