using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// PostgreSQL's internal type-name aliases (issue #197). None of these are keywords in the
/// grammar, so they arrive as generic type names; resolving them to the built-in they name is
/// what makes a column declared with the short spelling hash-match one extracted from the
/// catalog, which reports only the spelled-out name.
///
/// <para>
/// Every mapping asserted here was measured against <c>postgres:latest</c> by declaring a
/// column of the alias and reading back <c>information_schema.columns.data_type</c>, not
/// inferred from the grammar. The alias set is deliberately closed: a name is resolved only
/// where the server was observed to report the canonical type for it.
/// </para>
/// </summary>
public class TypeNameAliasTests
{
    private static DataType ParseColumnType(string typeText)
    {
        var parser = new AntlrPostgresParser();

        var root = parser.Parse($"""
CREATE TABLE t
(
    c {typeText}
);
""");

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var column = Assert.IsType<ColumnDefinition>(Assert.Single(createTable.Elements));

        return column.DataType;
    }

    [Theory]
    [InlineData("timetz", PostgresBuiltInDataType.TimeWithTimeZone)]
    [InlineData("timestamptz", PostgresBuiltInDataType.TimestampWithTimeZone)]
    [InlineData("varbit", PostgresBuiltInDataType.BitVarying)]
    [InlineData("bool", PostgresBuiltInDataType.Boolean)]
    [InlineData("int2", PostgresBuiltInDataType.SmallInt)]
    [InlineData("int4", PostgresBuiltInDataType.Integer)]
    [InlineData("int8", PostgresBuiltInDataType.BigInt)]
    [InlineData("float4", PostgresBuiltInDataType.Real)]
    [InlineData("float8", PostgresBuiltInDataType.Double)]
    [InlineData("serial2", PostgresBuiltInDataType.SmallSerial)]
    [InlineData("serial4", PostgresBuiltInDataType.Serial)]
    [InlineData("serial8", PostgresBuiltInDataType.BigSerial)]
    public void Alias_ResolvesToBuiltInType(string typeText, PostgresBuiltInDataType expected)
    {
        var dataType = Assert.IsType<BuiltInDataType>(ParseColumnType(typeText));

        Assert.Equal(expected, dataType.Type);
    }

    /// <summary>
    /// Unquoted identifiers are folded to lower case by PostgreSQL, so the alias must resolve
    /// however it is spelled.
    /// </summary>
    [Theory]
    [InlineData("TIMETZ", PostgresBuiltInDataType.TimeWithTimeZone)]
    [InlineData("TimestampTz", PostgresBuiltInDataType.TimestampWithTimeZone)]
    [InlineData("Int8", PostgresBuiltInDataType.BigInt)]
    [InlineData("VARBIT", PostgresBuiltInDataType.BitVarying)]
    public void Alias_ResolvesCaseInsensitively(string typeText, PostgresBuiltInDataType expected)
    {
        var dataType = Assert.IsType<BuiltInDataType>(ParseColumnType(typeText));

        Assert.Equal(expected, dataType.Type);
    }

    /// <summary>
    /// The source spelling is kept on the node, as it is for every other type: the model is
    /// canonicalized from <see cref="BuiltInDataType.Type"/>, and discarding what was written
    /// would lose the text any diagnostic quotes back to the author.
    /// </summary>
    [Theory]
    [InlineData("timetz")]
    [InlineData("int8")]
    [InlineData("VARBIT")]
    public void Alias_KeepsSourceSpelling(string typeText)
    {
        var dataType = Assert.IsType<BuiltInDataType>(ParseColumnType(typeText));

        Assert.Equal(typeText, dataType.TypeName);
    }

    /// <summary>
    /// An alias resolves to the same canonical name the catalog reports for it, which is the
    /// whole point: <c>timetz</c> and <c>time with time zone</c> are one type, and the two
    /// spellings must produce one model.
    /// </summary>
    [Theory]
    [InlineData("timetz", "time with time zone")]
    [InlineData("timestamptz", "timestamp with time zone")]
    [InlineData("varbit", "bit varying")]
    [InlineData("bool", "boolean")]
    [InlineData("int2", "smallint")]
    [InlineData("int4", "integer")]
    [InlineData("int8", "bigint")]
    [InlineData("float4", "real")]
    [InlineData("float8", "double precision")]
    public void Alias_CanonicalizesToTheNameTheCatalogReports(string typeText, string expected)
    {
        var dataType = Assert.IsType<BuiltInDataType>(ParseColumnType(typeText));

        Assert.Equal(expected, dataType.Type.CanonicalName());
    }

    /// <summary>
    /// <c>varbit(n)</c> carries its length exactly as <c>bit varying(n)</c> does; resolving the
    /// alias must not drop the modifier.
    /// </summary>
    [Fact]
    public void VarbitWithLength_KeepsItsModifier()
    {
        var dataType = Assert.IsType<BuiltInDataType>(ParseColumnType("varbit(8)"));

        Assert.Equal(PostgresBuiltInDataType.BitVarying, dataType.Type);

        var modifier = Assert.IsType<LiteralExpression>(Assert.Single(dataType.Modifiers));

        Assert.Equal(8L, modifier.Value);
    }

    /// <summary>
    /// An alias inside an array declaration resolves as the element type, so <c>int8[]</c> is
    /// the same model as <c>bigint[]</c>.
    /// </summary>
    [Fact]
    public void Alias_ResolvesAsAnArrayElementType()
    {
        var array = Assert.IsType<ArrayDataType>(ParseColumnType("int8[]"));

        var elementType = Assert.IsType<BuiltInDataType>(array.ElementType);

        Assert.Equal(PostgresBuiltInDataType.BigInt, elementType.Type);
    }

    /// <summary>
    /// <c>bpchar</c> aliases <c>character</c> only when it carries a length. Measured on
    /// postgres:latest, <c>bpchar(4)</c> is rendered <c>character(4)</c> by <c>format_type()</c>,
    /// identical to a column declared <c>character(4)</c>, so the length must survive the
    /// resolution.
    /// </summary>
    [Fact]
    public void BpcharWithLength_ResolvesToCharacterAndKeepsItsLength()
    {
        var dataType = Assert.IsType<BuiltInDataType>(ParseColumnType("bpchar(4)"));

        Assert.Equal(PostgresBuiltInDataType.Char, dataType.Type);

        var modifier = Assert.IsType<LiteralExpression>(Assert.Single(dataType.Modifiers));

        Assert.Equal(4L, modifier.Value);
    }

    /// <summary>
    /// A <em>bare</em> <c>bpchar</c> is deliberately not resolved to <c>character</c>: measured,
    /// a bare <c>character</c> column is <c>character(1)</c> while a bare <c>bpchar</c> column is
    /// unbounded, with <c>format_type()</c> reporting <c>bpchar</c>. They are different types, so
    /// folding one into the other would model a column as something the server disagrees with.
    /// </summary>
    [Fact]
    public void BareBpchar_IsNotTreatedAsAnAliasForCharacter()
    {
        var dataType = Assert.IsType<UnresolvedDataType>(ParseColumnType("bpchar"));

        Assert.Equal("bpchar", dataType.TypeName);
    }

    /// <summary>
    /// A user-defined type whose name merely resembles an alias is still a custom type. The
    /// alias set is closed and matched whole, so nothing outside it is captured.
    /// </summary>
    [Theory]
    [InlineData("int9")]
    [InlineData("boolish")]
    [InlineData("mytimetz")]
    public void NonAliasTypeName_StaysUnresolved(string typeText)
    {
        var dataType = Assert.IsType<UnresolvedDataType>(ParseColumnType(typeText));

        Assert.Equal(typeText, dataType.TypeName);
    }
}
