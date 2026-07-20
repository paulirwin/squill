namespace Squill.Provider.Postgres.Tests;

public class SqlNameTests
{
    [Fact]
    public void Object_RendersQuoted()
    {
        var name = SqlName.Object("film");

        Assert.Equal("\"film\"", name.ToString());
    }

    [Fact]
    public void SchemaQualifiedObject_RendersBothSegmentsQuoted()
    {
        var name = SqlName.Object("public", "film");

        Assert.Equal("\"public\".\"film\"", name.ToString());
    }

    [Fact]
    public void Child_AppendsQuotedChildSegment()
    {
        var table = SqlName.Object("film");

        var column = table.Child("title");

        Assert.Equal("\"film\".\"title\"", column.ToString());
    }

    [Fact]
    public void Child_OnSchemaQualifiedObject_KeepsAllSegments()
    {
        var table = SqlName.Object("public", "film");

        var column = table.Child("title");

        Assert.Equal("\"public\".\"film\".\"title\"", column.ToString());
    }

    [Fact]
    public void ImplicitConversionToString_MatchesToString()
    {
        var name = SqlName.Object("film");

        string asString = name;

        Assert.Equal(name.ToString(), asString);
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        Assert.Equal(SqlName.Object("public", "film"), SqlName.Object("public", "film"));
        Assert.NotEqual(SqlName.Object("public", "film"), SqlName.Object("film"));
    }

    [Fact]
    public void UnqualifiedName_IsBareIdentifier()
    {
        // The bare identifier (no schema, no quotes) is needed when the identifier
        // must appear on its own, e.g. the column list of a CREATE INDEX.
        var column = SqlName.Object("film").Child("title");

        Assert.Equal("title", column.UnqualifiedName);
    }

    [Fact]
    public void Quoted_ReturnsJustTheLastSegmentQuoted()
    {
        var column = SqlName.Object("film").Child("title");

        Assert.Equal("\"title\"", column.Quoted);
    }

    [Fact]
    public void UnqualifiedOf_ParsesLastSegmentFromCanonicalString()
    {
        Assert.Equal("title", SqlName.UnqualifiedOf("\"film\".\"title\""));
        Assert.Equal("film", SqlName.UnqualifiedOf("\"film\""));
        Assert.Equal("title", SqlName.UnqualifiedOf("\"public\".\"film\".\"title\""));
    }
}
