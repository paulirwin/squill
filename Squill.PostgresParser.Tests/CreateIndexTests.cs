using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

public class CreateIndexTests
{
    [Fact]
    public void Sakila_FilmFulltextIndex()
    {
        var parser = new AntlrPostgresParser();
        
        const string text = """
CREATE INDEX film_fulltext_idx ON film USING gist (fulltext);
""";
        
        var root = parser.Parse(text);
        Assert.NotNull(root);
        Assert.Equal(1, root.Statements.Count);

        var createIndex = Assert.IsType<CreateIndexStatement>(root.Statements[0]);

        var name = Assert.IsType<SimpleIdentifier>(createIndex.Name);
        Assert.Equal("film_fulltext_idx", name.Name);
        
        Assert.Equal("film", createIndex.OnRelation.Name.Segments[0].Name);
        Assert.Equal("gist", createIndex.UsingMethod?.Name);
        Assert.False(createIndex.Unique);
        Assert.False(createIndex.Concurrently);
        Assert.False(createIndex.IfNotExists);
        Assert.False(createIndex.OnRelation.Only);
        Assert.False(createIndex.OnRelation.Star);
        Assert.Equal(1, createIndex.Elements.Count);

        var col = Assert.IsType<ColumnReferenceExpression>(createIndex.Elements[0].Expression);
        Assert.Equal("fulltext", col.Identifier.Name);
    }
}