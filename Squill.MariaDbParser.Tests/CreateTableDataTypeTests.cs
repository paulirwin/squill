using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;
using Squill.TestFramework;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for CREATE TABLE column data types, asserting the syntax tree the
/// mapper produces. Model-level concerns (element shape, script generation) are covered in
/// Squill.Provider.MariaDb.Tests.
/// </summary>
public class CreateTableDataTypeTests
{
    private static CreateTableStatement ParseOne(string text)
        => ParseAssertions.Single<CreateTableStatement>(new AntlrMariaDbParser().Parse(text).Statements);

    private static DataType ColumnType(CreateTableStatement table, string columnName)
    {
        var column = table.Elements
            .OfType<ColumnDefinition>()
            .Single(c => c.Name.Name == columnName);

        return column.DataType;
    }

    // enum(...) columns must retain their value list verbatim (issue #73); without it the
    // generated DDL would drop the values and emit invalid "enum NULL".
    [Fact]
    public void EnumColumn_CapturesValueList()
    {
        var table = ParseOne(
            "CREATE TABLE film (rating enum('G','PG','PG-13','R','NC-17'));");

        var type = ColumnType(table, "rating");

        Assert.Equal("enum", type.TypeName);
        Assert.Equal(
            new[] { "'G'", "'PG'", "'PG-13'", "'R'", "'NC-17'" },
            type.CollectionValues);
    }

    [Fact]
    public void SetColumn_CapturesValueList()
    {
        var table = ParseOne(
            "CREATE TABLE film (special_features set('Trailers','Deleted Scenes'));");

        var type = ColumnType(table, "special_features");

        Assert.Equal("set", type.TypeName);
        Assert.Equal(new[] { "'Trailers'", "'Deleted Scenes'" }, type.CollectionValues);
    }

    // A non-collection type carries no collection values, so the collection handling never
    // leaks into ordinary columns.
    [Fact]
    public void ScalarColumn_HasNoCollectionValues()
    {
        var table = ParseOne("CREATE TABLE film (title varchar(255));");

        Assert.Empty(ColumnType(table, "title").CollectionValues);
    }

    // The parser lower-cases and preserves the written type name; alias canonicalization
    // (e.g. integer->int) happens later in the model builder, not here. Grounded in
    // https://mariadb.com/kb/en/data-types/.
    [Theory]
    [InlineData("tinyint", "tinyint")]
    [InlineData("SMALLINT", "smallint")]
    [InlineData("mediumint", "mediumint")]
    [InlineData("int", "int")]
    [InlineData("integer", "integer")]
    [InlineData("bigint", "bigint")]
    [InlineData("float", "float")]
    [InlineData("double", "double")]
    [InlineData("date", "date")]
    [InlineData("datetime", "datetime")]
    [InlineData("timestamp", "timestamp")]
    [InlineData("time", "time")]
    [InlineData("year", "year")]
    [InlineData("text", "text")]
    [InlineData("longtext", "longtext")]
    [InlineData("blob", "blob")]
    [InlineData("mediumblob", "mediumblob")]
    [InlineData("json", "json")]
    public void ScalarColumn_CapturesTypeName(string declared, string expected)
    {
        var table = ParseOne($"CREATE TABLE t (c {declared});");

        Assert.Equal(expected, ColumnType(table, "c").TypeName);
    }

    // A length modifier on a character or binary type is captured as a single modifier.
    [Theory]
    [InlineData("char(10)", "char", 10)]
    [InlineData("varchar(255)", "varchar", 255)]
    [InlineData("binary(16)", "binary", 16)]
    [InlineData("varbinary(255)", "varbinary", 255)]
    public void LengthColumn_CapturesLength(string declared, string expectedName, long expectedLength)
    {
        var type = ColumnType(ParseOne($"CREATE TABLE t (c {declared});"), "c");

        Assert.Equal(expectedName, type.TypeName);
        Assert.Equal(expectedLength, Assert.Single(type.Modifiers));
    }

    // decimal/numeric carry precision and scale as two modifiers.
    [Theory]
    [InlineData("decimal(10,2)", "decimal")]
    [InlineData("numeric(10,2)", "numeric")]
    public void DecimalColumn_CapturesPrecisionAndScale(string declared, string expectedName)
    {
        var type = ColumnType(ParseOne($"CREATE TABLE t (c {declared});"), "c");

        Assert.Equal(expectedName, type.TypeName);
        Assert.Equal(new long[] { 10, 2 }, type.Modifiers);
    }

    // UNSIGNED on an integer type is captured as a flag on the data type.
    [Fact]
    public void UnsignedColumn_CapturesUnsignedFlag()
    {
        var type = ColumnType(ParseOne("CREATE TABLE t (c int unsigned);"), "c");

        Assert.Equal("int", type.TypeName);
        Assert.True(type.IsUnsigned);
    }

    /// <summary>
    /// ZEROFILL is captured as a flag on the data type (issue #190). It was previously accepted
    /// by the grammar and then dropped by the mapper, which is fine while nothing asks about it,
    /// but a deprecation check that could not see it would miss every ZEROFILL column — worse
    /// than not checking at all. The type name and any display width are unaffected by it.
    /// </summary>
    [Theory]
    [InlineData("int zerofill", "int")]
    [InlineData("int(11) zerofill", "int")]
    [InlineData("bigint unsigned zerofill", "bigint")]
    [InlineData("decimal(10,2) zerofill", "decimal")]
    [InlineData("double zerofill", "double")]
    public void ZerofillColumn_CapturesZerofillFlag(string declared, string expectedName)
    {
        var type = ColumnType(ParseOne($"CREATE TABLE t (c {declared});"), "c");

        Assert.Equal(expectedName, type.TypeName);
        Assert.True(type.IsZerofill);
    }

    [Fact]
    public void ColumnWithoutZerofill_DoesNotSetZerofillFlag()
    {
        var type = ColumnType(ParseOne("CREATE TABLE t (c int(11) unsigned);"), "c");

        Assert.False(type.IsZerofill);
        Assert.True(type.IsUnsigned);
    }

    /// <summary>
    /// A type-level CHARACTER SET is captured (issue #190). Like ZEROFILL, the grammar accepted
    /// it and the mapper discarded it; the <c>utf8</c> alias for <c>utf8mb3</c> is deprecated, and
    /// a check cannot report what the syntax tree does not carry.
    /// </summary>
    [Theory]
    [InlineData("varchar(10) character set utf8", "utf8")]
    [InlineData("varchar(10) CHARACTER SET utf8mb4", "utf8mb4")]
    [InlineData("text character set utf8mb3", "utf8mb3")]
    public void ColumnWithCharacterSet_CapturesCharacterSet(string declared, string expected)
    {
        var type = ColumnType(ParseOne($"CREATE TABLE t (c {declared});"), "c");

        Assert.Equal(expected, type.CharacterSet);
    }

    [Fact]
    public void ColumnWithoutCharacterSet_HasNullCharacterSet()
    {
        var type = ColumnType(ParseOne("CREATE TABLE t (c varchar(10));"), "c");

        Assert.Null(type.CharacterSet);
    }

    /// <summary>
    /// A CHARACTER SET on an enum/set type lands on a different grammar alternative than the
    /// string types', so it is asserted separately — the two are easy to fix one of and not
    /// the other.
    /// </summary>
    [Fact]
    public void EnumColumnWithCharacterSet_CapturesCharacterSet()
    {
        var type = ColumnType(
            ParseOne("CREATE TABLE t (c enum('a','b') character set utf8);"), "c");

        Assert.Equal("enum", type.TypeName);
        Assert.Equal("utf8", type.CharacterSet);
        Assert.Equal(new[] { "'a'", "'b'" }, type.CollectionValues);
    }

    /// <summary>
    /// The national-character types and their many spellings (issue #162). The parser keeps the
    /// written name, as everywhere else here — the fold to what the engines store
    /// (<c>varchar</c>/<c>char</c>) belongs to the model builder.
    ///
    /// The point of the test is the length: it lands on a different grammar alternative for each
    /// spelling, and the VARYING spellings used to fall through to the raw-type-name default,
    /// which discards modifiers. A national type whose length is dropped generates a bare
    /// <c>nvarchar</c> that both engines reject as a syntax error.
    ///
    /// The VARYING spellings report <c>nvarchar</c> rather than the <c>char</c>/<c>nchar</c> the
    /// grammar labels them with: they are varying types, and reporting the non-varying name would
    /// fold a varchar column down to a fixed-width char.
    /// </summary>
    [Theory]
    [InlineData("nvarchar(45)", "nvarchar", 45)]
    [InlineData("NATIONAL VARCHAR(45)", "varchar", 45)]
    [InlineData("NATIONAL CHARACTER VARYING(45)", "nvarchar", 45)]
    [InlineData("NATIONAL CHAR VARYING(45)", "nvarchar", 45)]
    [InlineData("NCHAR VARYING(45)", "nvarchar", 45)]
    [InlineData("nchar(10)", "nchar", 10)]
    [InlineData("NATIONAL CHAR(10)", "char", 10)]
    [InlineData("NATIONAL CHARACTER(10)", "character", 10)]
    public void NationalCharacterColumn_CapturesTypeNameAndLength(
        string declared, string expectedName, long expectedLength)
    {
        var type = ColumnType(ParseOne($"CREATE TABLE t (c {declared});"), "c");

        Assert.Equal(expectedName, type.TypeName);
        Assert.Equal(expectedLength, Assert.Single(type.Modifiers));
    }

    // REAL is a floating-point synonym; the parser keeps the written word and the model builder
    // folds it to the `double` both engines store (issue #162).
    [Fact]
    public void RealColumn_CapturesTypeName()
        => Assert.Equal("real", ColumnType(ParseOne("CREATE TABLE t (c REAL);"), "c").TypeName);
}
