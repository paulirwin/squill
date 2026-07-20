using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

public class VectorTypeTests
{
    [Fact]
    public void CreateTable_VectorColumn_ParsesDimensionModifier()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE TABLE items (embedding vector(3));";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var column = Assert.Single(stmt.Elements.OfType<ColumnDefinition>());
        Assert.Equal("embedding", column.Name.Name);

        // vector is not a built-in type, so it maps to an UnresolvedDataType whose
        // name is "vector" and which carries the dimension as a single modifier.
        var dataType = Assert.IsType<UnresolvedDataType>(column.DataType);
        Assert.Equal("vector", dataType.TypeName);

        var modifier = Assert.Single(dataType.Modifiers);
        var literal = Assert.IsType<LiteralExpression>(modifier);
        Assert.Equal(3L, literal.Value);
    }

    [Fact]
    public void CreateTable_VectorColumn_NoDimension_ParsesWithoutModifiers()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE TABLE items (embedding vector);";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var column = Assert.Single(stmt.Elements.OfType<ColumnDefinition>());

        var dataType = Assert.IsType<UnresolvedDataType>(column.DataType);
        Assert.Equal("vector", dataType.TypeName);
        Assert.Empty(dataType.Modifiers);
    }
}
