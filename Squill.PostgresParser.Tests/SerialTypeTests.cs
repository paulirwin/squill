using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// SERIAL / SMALLSERIAL / BIGSERIAL parsing (issue #121). These are not real Postgres
/// types — they are notational shorthand for an integer column backed by a sequence — so
/// the parser recognizes them as their own built-in kinds and the provider is responsible
/// for lowering them to the underlying integer type plus identity.
/// </summary>
public class SerialTypeTests
{
    [Theory]
    [InlineData("smallserial", PostgresBuiltInDataType.SmallSerial)]
    [InlineData("serial", PostgresBuiltInDataType.Serial)]
    [InlineData("bigserial", PostgresBuiltInDataType.BigSerial)]
    [InlineData("SERIAL", PostgresBuiltInDataType.Serial)]
    [InlineData("BigSerial", PostgresBuiltInDataType.BigSerial)]
    public void SerialTypes_MapToBuiltInDataType(string typeText, PostgresBuiltInDataType expected)
    {
        var parser = new AntlrPostgresParser();

        var text = $"""
CREATE TABLE widgets
(
    id {typeText}
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var idColumn = Assert.IsType<ColumnDefinition>(Assert.Single(createTable.Elements));

        var dataType = Assert.IsType<BuiltInDataType>(idColumn.DataType);

        Assert.Equal(expected, dataType.Type);
    }

    /// <summary>
    /// A serial column is a sequence-backed integer, so its canonical name must be the
    /// underlying integer type that the database actually reports (via format_type() /
    /// information_schema). Postgres never reports a column's type as "serial", so
    /// canonicalizing to "serial" would guarantee a parsed model could never hash-match one
    /// extracted from a live database.
    /// </summary>
    [Theory]
    [InlineData(PostgresBuiltInDataType.SmallSerial, "smallint")]
    [InlineData(PostgresBuiltInDataType.Serial, "integer")]
    [InlineData(PostgresBuiltInDataType.BigSerial, "bigint")]
    public void SerialTypes_CanonicalizeToUnderlyingIntegerType(
        PostgresBuiltInDataType dataType, string expected)
    {
        Assert.Equal(expected, dataType.CanonicalName());
    }

    [Fact]
    public void Serial_CombinedWithPrimaryKey()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE widgets
(
    id serial PRIMARY KEY,
    name text NOT NULL
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var idColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[0]);

        var dataType = Assert.IsType<BuiltInDataType>(idColumn.DataType);

        Assert.Equal(PostgresBuiltInDataType.Serial, dataType.Type);
        Assert.IsType<PrimaryKeyColumnConstraint>(Assert.Single(idColumn.Constraints));
    }
}
