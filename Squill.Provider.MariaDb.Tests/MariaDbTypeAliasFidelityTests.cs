using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Unit coverage for issue #162: the national-character and <c>REAL</c> type aliases must
/// canonicalize to the type the engines actually store, not to the word that was written.
///
/// Both engines were measured to agree exactly. <c>NVARCHAR(45)</c>, <c>NATIONAL VARCHAR(45)</c>
/// and <c>NCHAR VARYING(45)</c> are all stored as <c>varchar(45)</c>; <c>NCHAR(10)</c> and
/// <c>NATIONAL CHAR(10)</c> as <c>char(10)</c>; and <c>REAL</c> as <c>double</c> (a documented
/// synonym unless <c>REAL_AS_FLOAT</c> is set, which is not the default on either engine).
///
/// Getting this wrong failed in two distinct ways. An uncanonicalized <c>nvarchar</c> fell
/// outside the script generator's set of length-carrying types, so the generated DDL was a bare
/// <c>nvarchar</c> that both engines reject as a syntax error — a build that passed and a deploy
/// that did not. An uncanonicalized <c>real</c> deployed fine but never matched the extracted
/// <c>double</c>, so unchanged source re-diffed on every deploy.
/// </summary>
public class MariaDbTypeAliasFidelityTests
{
    [Theory]
    // The national-character aliases. Every spelling reaches varchar or char, and the declared
    // length has to survive the fold — a length dropped here is invalid SQL, not a fidelity nit.
    [InlineData("nvarchar", "varchar")]
    [InlineData("NVARCHAR", "varchar")]
    [InlineData("nchar", "char")]
    [InlineData("NCHAR", "char")]
    // CHARACTER is the SQL-standard synonym for CHAR.
    [InlineData("character", "char")]
    [InlineData("CHARACTER", "char")]
    // REAL is DOUBLE on both engines by default.
    [InlineData("real", "double")]
    [InlineData("REAL", "double")]
    public void Canonicalize_TypeAlias_ResolvesToTheStoredType(string written, string expected)
        => Assert.Equal(expected, MariaDbTypeNormalizer.Canonicalize(written));

    /// <summary>
    /// The canonical spellings, and the aliases already folded before #162, must be unaffected.
    /// </summary>
    [Theory]
    [InlineData("varchar", "varchar")]
    [InlineData("char", "char")]
    [InlineData("double", "double")]
    [InlineData("float", "float")]
    [InlineData("integer", "int")]
    [InlineData("dec", "decimal")]
    public void Canonicalize_AlreadyCanonicalOrPreviouslyFoldedType_IsUnchanged(
        string written, string expected)
        => Assert.Equal(expected, MariaDbTypeNormalizer.Canonicalize(written));

    /// <summary>
    /// A routine parameter takes the same fold, and a folded national type keeps its length —
    /// the normalizer's length handling is keyed off the canonical name.
    /// </summary>
    [Fact]
    public void Normalize_NationalVarcharParameter_KeepsItsLength()
        => Assert.Equal("varchar(45)", MariaDbTypeNormalizer.Normalize("NVARCHAR", [45], false));

    [Fact]
    public void Normalize_RealParameter_FoldsToDouble()
        => Assert.Equal("double", MariaDbTypeNormalizer.Normalize("REAL", [], false));

    // -------------------------------------------------------------------------------------------
    // The model side: a parsed column's type specifier must carry the canonical name and, for a
    // character type, the declared length. Canonicalizing the name is what makes the length
    // survive — the length-carrying check is keyed off the canonical name, so an unfolded alias
    // falls outside it and loses its length.
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("NVARCHAR(45)", "varchar", 45)]
    [InlineData("NATIONAL VARCHAR(45)", "varchar", 45)]
    [InlineData("NCHAR VARYING(45)", "varchar", 45)]
    [InlineData("NATIONAL CHARACTER VARYING(45)", "varchar", 45)]
    [InlineData("NCHAR(10)", "char", 10)]
    [InlineData("NATIONAL CHAR(10)", "char", 10)]
    [InlineData("NATIONAL CHARACTER(10)", "char", 10)]
    // CHARACTER is the SQL-standard synonym for CHAR, and CHAR VARYING for VARCHAR. They are
    // folded by the same rules, so a length-carrying CHARACTER column no longer renders as a
    // bare `character` either.
    [InlineData("CHARACTER(10)", "char", 10)]
    [InlineData("CHAR VARYING(45)", "varchar", 45)]
    [InlineData("CHARACTER VARYING(45)", "varchar", 45)]
    public async Task ExtractModel_CharacterTypeAliasColumn_ModelsTheStoredTypeAndLength(
        string declared, string expectedType, int expectedLength)
    {
        var typeSpec = await ColumnTypeSpecifierAsync(declared);

        Assert.Equal(expectedType, TypeNameOf(typeSpec));
        Assert.Equal(expectedLength, typeSpec.GetProperty<int?>(MariaDbPropertyNames.Length));
    }

    [Fact]
    public async Task ExtractModel_RealColumn_ModelsDouble()
    {
        var typeSpec = await ColumnTypeSpecifierAsync("REAL");

        Assert.Equal("double", TypeNameOf(typeSpec));
    }

    // -------------------------------------------------------------------------------------------
    // The script side: the generated DDL must be valid SQL for both engines.
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("NVARCHAR(45)", "`c` varchar(45) NULL")]
    [InlineData("NATIONAL VARCHAR(45)", "`c` varchar(45) NULL")]
    [InlineData("NCHAR VARYING(45)", "`c` varchar(45) NULL")]
    [InlineData("NCHAR(10)", "`c` char(10) NULL")]
    [InlineData("REAL", "`c` double NULL")]
    public async Task GenerateScript_TypeAliasColumn_EmitsTheStoredType(
        string declared, string expected)
    {
        var script = await ScriptAsync($"""
            CREATE TABLE t
            (
                c {declared} NULL
            );
            """);

        Assert.Contains(expected, script);
    }

    private static async Task<Element> ColumnTypeSpecifierAsync(string declaredType)
    {
        var model = await BuildAsync($"""
            CREATE TABLE t
            (
                c {declaredType} NULL
            );
            """);

        var table = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlTable);
        var columns = table.GetRelationship(MariaDbRelationshipNames.Columns)!;
        var column = Assert.IsType<Element>(columns.Entries[0]);

        var typeSpecifier = column.GetRelationship(MariaDbRelationshipNames.TypeSpecifier)!;

        return Assert.Single(typeSpecifier.Entries.OfType<Element>());
    }

    private static string TypeNameOf(Element typeSpecifier)
    {
        var type = typeSpecifier.GetRelationship(MariaDbRelationshipNames.Type)!;

        return Assert.Single(type.Entries.OfType<Reference>()).Name;
    }

    private static async Task<Model> BuildAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(
            workspace, new AntlrMariaDbParser(), new MariaDb12DatabaseSchemaProvider());

        return (await builder.ExtractModelAsync()).Model;
    }

    private static async Task<string> ScriptAsync(string sql)
    {
        var provider = new MariaDbDatabaseProvider("Server=unused");
        var comparison = SchemaCompare.Compare(provider, await BuildAsync(sql), new Model());

        return new MariaDbScriptGenerator().GenerateScript(comparison);
    }
}
