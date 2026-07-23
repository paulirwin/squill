using Squill.PostgresParser.Syntax;
using Squill.TestFramework;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Parser-level coverage for the <c>bit</c> / <c>bit varying</c> and <c>interval</c> column
/// types (issue #97). Before the fix both branches of <c>simpletypename</c> were unhandled in
/// <c>VisitTypename</c> and any such column threw <c>NotImplementedException</c> at parse time.
/// Grounded in the Postgres docs:
/// https://www.postgresql.org/docs/current/datatype-bit.html and
/// https://www.postgresql.org/docs/current/datatype-datetime.html#DATATYPE-INTERVAL-INPUT.
/// </summary>
public class BitAndIntervalTypeTests
{
    private static ColumnDefinition ParseColumn(string columnSql)
    {
        var root = new AntlrPostgresParser().Parse($"CREATE TABLE t ({columnSql});");
        var stmt = ParseAssertions.Single<CreateTableStatement>(root.Statements);
        return Assert.Single(stmt.Elements.OfType<ColumnDefinition>());
    }

    [Fact]
    public void FixedLengthBit_ParsesAsBitWithLength()
    {
        var type = Assert.IsType<BuiltInDataType>(ParseColumn("flags bit(8)").DataType);

        Assert.Equal(PostgresBuiltInDataType.Bit, type.Type);
        var modifier = Assert.IsType<LiteralExpression>(Assert.Single(type.Modifiers));
        Assert.Equal(8L, modifier.Value);
    }

    [Fact]
    public void BareBit_ParsesAsBitWithNoModifier()
    {
        // A bare `bit` is fixed-length bit(1); no explicit length modifier is present in the
        // source, so none is captured here (the bit(1) default is applied at model-build time).
        var type = Assert.IsType<BuiltInDataType>(ParseColumn("flag bit").DataType);

        Assert.Equal(PostgresBuiltInDataType.Bit, type.Type);
        Assert.Empty(type.Modifiers);
    }

    [Fact]
    public void BitVaryingWithLength_ParsesAsBitVarying()
    {
        var type = Assert.IsType<BuiltInDataType>(ParseColumn("flags bit varying(16)").DataType);

        Assert.Equal(PostgresBuiltInDataType.BitVarying, type.Type);
        var modifier = Assert.IsType<LiteralExpression>(Assert.Single(type.Modifiers));
        Assert.Equal(16L, modifier.Value);
    }

    [Fact]
    public void BareBitVarying_ParsesAsBitVaryingWithNoModifier()
    {
        var type = Assert.IsType<BuiltInDataType>(ParseColumn("flags bit varying").DataType);

        Assert.Equal(PostgresBuiltInDataType.BitVarying, type.Type);
        Assert.Empty(type.Modifiers);
    }

    [Fact]
    public void Interval_ParsesAsInterval()
    {
        var type = Assert.IsType<BuiltInDataType>(ParseColumn("duration interval").DataType);

        Assert.Equal(PostgresBuiltInDataType.Interval, type.Type);
    }

    [Fact]
    public void IntervalWithFieldSpec_ParsesAsInterval()
    {
        // A field-qualified interval (e.g. `interval day to second`) still maps to the
        // canonical interval type; the qualifier is preserved in the type's original text.
        var type = Assert.IsType<BuiltInDataType>(ParseColumn("duration interval day to second").DataType);

        Assert.Equal(PostgresBuiltInDataType.Interval, type.Type);
    }
}
