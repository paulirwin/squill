using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for CREATE TABLE column data types, asserting the syntax tree the
/// mapper produces. Model-level concerns (element shape, script generation) are covered in
/// Squill.Provider.MariaDb.Tests.
/// </summary>
public class CreateTableDataTypeTests
{
    private static CreateTableStatement ParseOne(string text)
    {
        var root = new AntlrMariaDbParser().Parse(text);

        return Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
    }

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
}
