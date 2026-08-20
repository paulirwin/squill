using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Type-level modifiers that were parsed and then discarded (issue #217).
///
/// <para>
/// The two character-set forms are the ones that matter: a per-column <c>CHARACTER SET</c> and
/// the <c>BINARY</c> suffix each decide the column's collation, which decides comparison and
/// sort order. Dropping them does not merely lose a cosmetic facet, it deploys a column that
/// compares differently from the one that was declared, with no diagnostic.
/// </para>
///
/// <para>
/// Neither is modeled as itself. Measured on all four supported majors, the engines resolve both
/// to a collation and report only that: <c>CHARACTER SET latin1</c> reads back as
/// <c>latin1_swedish_ci</c> and <c>VARCHAR(10) BINARY</c> as <c>utf8mb4_bin</c>, with
/// <c>information_schema</c> keeping no trace of which spelling produced it. So both are
/// resolved to the collation they imply and stored in the existing <c>Collation</c> property,
/// which is the only shape the extractor can ever match.
/// </para>
/// </summary>
public class MariaDbTypeModifierTests
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

    private static Element TypeSpecifierOf(Element column)
        => Assert.Single(
            column.GetRelationship(MariaDbRelationshipNames.TypeSpecifier)!.Entries.OfType<Element>());

    private static string TypeNameOf(Element column)
        => Assert.Single(
            TypeSpecifierOf(column).GetRelationship(MariaDbRelationshipNames.Type)!
                .Entries.OfType<Reference>()).Name;

    // ---- CHARACTER SET ----

    /// <summary>
    /// A per-column character set resolves to that character set's default collation, which is
    /// the only thing the catalog reports back.
    /// </summary>
    [Fact]
    public async Task CharacterSet_IsModeledAsItsDefaultCollation()
    {
        var (model, warnings) = await BuildAsync(
            "CREATE TABLE t (c varchar(10) CHARACTER SET latin1);");

        Assert.Equal("latin1_swedish_ci",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// An explicit COLLATE alongside a CHARACTER SET wins: the charset only supplies a default,
    /// so naming both stores the collation that was named, not the charset's.
    /// </summary>
    [Fact]
    public async Task CharacterSet_WithExplicitCollate_KeepsTheDeclaredCollation()
    {
        var (model, warnings) = await BuildAsync(
            "CREATE TABLE t (c char(5) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin);");

        Assert.Equal("utf8mb4_bin",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// The charset-to-collation map is a measured, per-major divergence, so it lives on the
    /// schema provider like every other engine capability. <c>utf8mb4</c> is the case that
    /// proves it: measured, it defaults to <c>utf8mb4_general_ci</c> on MariaDB 10,
    /// <c>utf8mb4_uca1400_ai_ci</c> on MariaDB 11 and 12, and <c>utf8mb4_0900_ai_ci</c> on
    /// MySQL 8 and 9. A single hardcoded table would be wrong on three of the four.
    /// </summary>
    [Theory]
    [InlineData(typeof(MariaDb10DatabaseSchemaProvider), "utf8mb4_general_ci")]
    [InlineData(typeof(MariaDb11DatabaseSchemaProvider), "utf8mb4_uca1400_ai_ci")]
    [InlineData(typeof(MariaDb12DatabaseSchemaProvider), "utf8mb4_uca1400_ai_ci")]
    [InlineData(typeof(MySql8DatabaseSchemaProvider), "utf8mb4_0900_ai_ci")]
    [InlineData(typeof(MySql9DatabaseSchemaProvider), "utf8mb4_0900_ai_ci")]
    public void DefaultCollationForCharacterSet_Utf8Mb4_IsPerMajor(Type engineType, string expected)
    {
        var engine = (MariaDbFamilyDatabaseSchemaProvider)Activator.CreateInstance(engineType)!;

        Assert.Equal(expected, engine.DefaultCollationForCharacterSet("utf8mb4"));
    }

    /// <summary>
    /// The charsets whose default collation is the same on every supported major. These are
    /// stated per-engine anyway rather than special-cased, so that a future major changing one
    /// is a one-line edit in that major rather than a rule that silently stops holding.
    /// </summary>
    [Theory]
    [InlineData("latin1", "latin1_swedish_ci")]
    [InlineData("ascii", "ascii_general_ci")]
    [InlineData("binary", "binary")]
    public void DefaultCollationForCharacterSet_StableCharacterSets_AgreeAcrossEngines(
        string characterSet, string expected)
    {
        Assert.Equal(expected, new MariaDb12DatabaseSchemaProvider()
            .DefaultCollationForCharacterSet(characterSet));
        Assert.Equal(expected, new MySql9DatabaseSchemaProvider()
            .DefaultCollationForCharacterSet(characterSet));
    }

    /// <summary>
    /// A character set Squill has no measured collation for is not guessed at. Returning null
    /// leaves the column carrying no collation, which is the pre-existing behaviour, rather than
    /// inventing a <c>&lt;charset&gt;_general_ci</c> that the server may not agree with, a
    /// wrong collation deploys a wrong column, where an absent one deploys the server's own
    /// default.
    /// </summary>
    [Fact]
    public void DefaultCollationForCharacterSet_UnknownCharacterSet_IsNull()
        => Assert.Null(new MariaDb12DatabaseSchemaProvider()
            .DefaultCollationForCharacterSet("nosuchcharset"));

    /// <summary>
    /// A character set Squill has no measured collation for records nothing and warns, rather
    /// than dropping the clause silently as before. The column still deploys; what it loses is
    /// the guarantee that its collation matches the source, which is what the warning says.
    ///
    /// <para>
    /// <c>swe7</c> stands in for the ~35 character sets the grammar accepts but that have not
    /// been measured. It is written quoted because that is also the spelling an entirely
    /// unrecognized name would have to take: <c>charsetName</c> admits a fixed keyword list, a
    /// string literal or a backtick-quoted name, so a name outside the keyword list only
    /// reaches the model builder via one of the quoted forms.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("'swe7'")]
    [InlineData("swe7")]
    public async Task CharacterSet_WithoutAMeasuredCollation_WarnsAndRecordsNoCollation(
        string characterSet)
    {
        var (model, warnings) = await BuildAsync(
            $"CREATE TABLE t (c varchar(10) CHARACTER SET {characterSet});");

        Assert.Null(SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));

        var warning = Assert.Single(warnings);
        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
        Assert.Contains("swe7", warning.Message);
    }

    /// <summary>
    /// A quoted character set names the same thing as a bare one, so both resolve identically.
    /// The quotes are part of the spelling, not of the name.
    /// </summary>
    [Theory]
    [InlineData("latin1")]
    [InlineData("'latin1'")]
    [InlineData("`latin1`")]
    public async Task CharacterSet_QuotedOrBare_ResolvesTheSame(string characterSet)
    {
        var (model, warnings) = await BuildAsync(
            $"CREATE TABLE t (c varchar(10) CHARACTER SET {characterSet});");

        Assert.Equal("latin1_swedish_ci",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Declaring the character set whose collation the column would inherit anyway records
    /// nothing, for the same reason a redundant COLLATE does (issue #216): the catalog reports
    /// the same value either way, so recording it would re-diff on every deploy.
    /// </summary>
    [Fact]
    public async Task CharacterSet_ResolvingToTheInheritedCollation_RecordsNothing()
    {
        var engine = new MariaDb12DatabaseSchemaProvider();

        var (model, warnings) = await BuildAsync(
            "CREATE TABLE t (c varchar(10) CHARACTER SET utf8mb4);", engine);

        Assert.Equal(engine.DefaultCollation, engine.DefaultCollationForCharacterSet("utf8mb4"));
        Assert.Null(SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    // ---- BINARY suffix ----

    /// <summary>
    /// The BINARY suffix is not a type of its own: it selects the binary collation of whichever
    /// character set the column has. With no CHARACTER SET of its own, that is the one inherited
    /// from the table, and with no table COLLATE either, the engine's default.
    /// </summary>
    [Fact]
    public async Task BinarySuffix_IsModeledAsTheBinaryCollationOfTheInheritedCharacterSet()
    {
        var (model, warnings) = await BuildAsync("CREATE TABLE t (c varchar(10) BINARY);");

        Assert.Equal("utf8mb4_bin",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// With a CHARACTER SET on the same column, BINARY takes that charset's binary collation
    /// rather than the inherited one.
    /// </summary>
    [Fact]
    public async Task BinarySuffix_WithCharacterSet_UsesThatCharacterSetsBinaryCollation()
    {
        var (model, warnings) = await BuildAsync(
            "CREATE TABLE t (c varchar(10) CHARACTER SET latin1 BINARY);");

        Assert.Equal("latin1_bin",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A table-level COLLATE changes what BINARY resolves against, because the character set is
    /// the collation's prefix. Measured: in a <c>COLLATE=latin1_general_ci</c> table, a
    /// <c>VARCHAR(10) BINARY</c> column reports <c>latin1_bin</c>.
    /// </summary>
    [Fact]
    public async Task BinarySuffix_InATableWithItsOwnCollation_ResolvesAgainstThatCharacterSet()
    {
        var (model, warnings) = await BuildAsync(
            """
            CREATE TABLE t (c varchar(10) BINARY)
            COLLATE=latin1_general_ci;
            """);

        Assert.Equal("latin1_bin",
            SingleColumn(model, "c").GetProperty<string>(MariaDbPropertyNames.Collation));
        Assert.Empty(warnings);
    }

    // ---- LONG and LONG VARBINARY ----

    /// <summary>
    /// <c>LONG</c> and <c>LONG VARCHAR</c> are synonyms for <c>MEDIUMTEXT</c>, and
    /// <c>LONG VARBINARY</c> for <c>MEDIUMBLOB</c> (measured on both engines). Before this they
    /// were modeled under the bare first token, <c>long</c>, which is not a type either engine
    /// has: the generated DDL was rejected outright.
    /// </summary>
    [Theory]
    [InlineData("LONG", "mediumtext")]
    [InlineData("LONG VARCHAR", "mediumtext")]
    [InlineData("LONG VARBINARY", "mediumblob")]
    public async Task LongTypes_AreModeledAsTheTypeTheEnginesStore(string declared, string expected)
    {
        var (model, warnings) = await BuildAsync($"CREATE TABLE t (c {declared});");

        Assert.Equal(expected, TypeNameOf(SingleColumn(model, "c")));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// The distinction that makes this worth getting right: the binary and character spellings
    /// differ only in the trailing keyword, and folding both to the first token collapsed them
    /// onto one another. A blob column deployed as text silently changes the column's semantics.
    /// </summary>
    [Fact]
    public async Task LongTypes_CharacterAndBinarySpellings_AreNotCollapsed()
    {
        var (model, _) = await BuildAsync(
            "CREATE TABLE t (a LONG VARCHAR, b LONG VARBINARY);");

        Assert.NotEqual(
            TypeNameOf(SingleColumn(model, "a")),
            TypeNameOf(SingleColumn(model, "b")));
    }

    // ---- VECTOR ----

    /// <summary>
    /// A <c>VECTOR(n)</c> column's dimension is part of its type: both engines report
    /// <c>COLUMN_TYPE</c> as <c>vector(3)</c>, and the dimension is not optional on either. The
    /// parser captured it already; it stopped at the model, because the length was recorded only
    /// for the character types.
    /// </summary>
    [Fact]
    public async Task Vector_KeepsItsDimension()
    {
        var (model, warnings) = await BuildAsync("CREATE TABLE t (c VECTOR(3));");

        var column = SingleColumn(model, "c");

        Assert.Equal("vector", TypeNameOf(column));
        Assert.Equal(3, TypeSpecifierOf(column).GetProperty<int?>(MariaDbPropertyNames.Length));
        Assert.Empty(warnings);
    }

    // ---- Generated DDL ----

    /// <summary>
    /// What the model holds has to survive into the DDL, or the round trip proves nothing. The
    /// character-set forms are emitted as the collation they resolved to, which is both what the
    /// engines store and what they report back.
    /// </summary>
    [Theory]
    [InlineData("varchar(10) CHARACTER SET latin1", "`c` varchar(10) COLLATE latin1_swedish_ci")]
    [InlineData("varchar(10) BINARY", "`c` varchar(10) COLLATE utf8mb4_bin")]
    [InlineData("LONG VARBINARY", "`c` mediumblob")]
    [InlineData("LONG VARCHAR", "`c` mediumtext")]
    [InlineData("VECTOR(3)", "`c` vector(3)")]
    public async Task GenerateScript_ModifiedType_EmitsWhatTheEnginesStore(
        string declared, string expected)
    {
        var (model, _) = await BuildAsync($"CREATE TABLE t (c {declared});");

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var script = new MariaDbScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));

        Assert.Contains(expected, script);
    }
}
