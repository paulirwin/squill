namespace Squill.Provider.Postgres.Tests;

public class SqlNameTests
{
    [Fact]
    public void ToString_RendersUnquoted()
    {
        // The in-memory / canonical form is unquoted; quoting is a SQL concern.
        var name = SqlName.Object("film");

        Assert.Equal("film", name.ToString());
    }

    [Fact]
    public void SchemaQualifiedObject_RendersDotJoinedUnquoted()
    {
        var name = SqlName.Object("public", "film");

        Assert.Equal("public.film", name.ToString());
    }

    [Fact]
    public void Child_AppendsSegment()
    {
        var table = SqlName.Object("film");

        var column = table.Child("title");

        Assert.Equal("film.title", column.ToString());
    }

    [Fact]
    public void Child_OnSchemaQualifiedObject_KeepsAllSegments()
    {
        var table = SqlName.Object("public", "film");

        var column = table.Child("title");

        Assert.Equal("public.film.title", column.ToString());
    }

    [Fact]
    public void ImplicitConversionToString_MatchesToString()
    {
        var name = SqlName.Object("film");

        string asString = name;

        Assert.Equal(name.ToString(), asString);
    }

    [Fact]
    public void Sql_RendersFullyQualifiedQuoted()
    {
        // The quoted rendering is only produced on demand, for emitting SQL.
        Assert.Equal("\"film\"", SqlName.Object("film").Sql);
        Assert.Equal("\"public\".\"film\".\"title\"", SqlName.Object("public", "film").Child("title").Sql);
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
        var column = SqlName.Object("film").Child("title");

        Assert.Equal("title", column.UnqualifiedName);
    }

    [Fact]
    public void QuotedUnqualified_ReturnsJustTheLastSegmentQuoted()
    {
        // For contexts that need the bare, quoted identifier, e.g. a CREATE INDEX
        // column list.
        var column = SqlName.Object("film").Child("title");

        Assert.Equal("\"title\"", column.QuotedUnqualified);
    }

    [Fact]
    public void UnqualifiedOf_ParsesLastSegmentFromCanonicalString()
    {
        Assert.Equal("title", SqlName.UnqualifiedOf("film.title"));
        Assert.Equal("film", SqlName.UnqualifiedOf("film"));
        Assert.Equal("title", SqlName.UnqualifiedOf("public.film.title"));
    }

    [Fact]
    public void Parse_RoundTripsCanonicalString()
    {
        var parsed = SqlName.Parse("public.film.title");

        Assert.Equal("public.film.title", parsed.ToString());
        Assert.Equal("\"public\".\"film\".\"title\"", parsed.Sql);
        Assert.Equal(SqlName.Object("public", "film", "title"), parsed);
    }

    [Fact]
    public void Sibling_ReplacesLastSegmentKeepingSchema()
    {
        // An index lives in its table's schema: sibling of public.film is public.idx.
        Assert.Equal("public.idx", SqlName.Object("public", "film").Sibling("idx").ToString());
        Assert.Equal("idx", SqlName.Object("film").Sibling("idx").ToString());
    }
}
