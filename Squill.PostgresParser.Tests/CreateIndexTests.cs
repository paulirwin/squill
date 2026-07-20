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
        Assert.Single(root.Statements);

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
        Assert.Single(createIndex.Elements);

        var col = Assert.IsType<ColumnReferenceExpression>(createIndex.Elements[0].Expression);
        Assert.Equal("fulltext", col.Identifier.Name);
    }

    [Fact]
    public void PartialIndex_WhereIsNotNull()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE INDEX idx_email ON users (email) WHERE email IS NOT NULL;
""";

        var root = parser.Parse(text);
        var createIndex = Assert.IsType<CreateIndexStatement>(Assert.Single(root.Statements));

        Assert.NotNull(createIndex.WhereClause);
        var unary = Assert.IsType<UnaryExpression>(createIndex.WhereClause);
        Assert.Equal(PostgresBuiltInUnaryOperator.IsNotNull, unary.Operator);
        var col = Assert.IsType<ColumnReferenceExpression>(unary.Expression);
        Assert.Equal("email", col.Identifier.Name);
    }

    [Fact]
    public void PartialIndex_WhereComparison()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE INDEX idx_active ON orders (customer_id) WHERE status = 'active';
""";

        var root = parser.Parse(text);
        var createIndex = Assert.IsType<CreateIndexStatement>(Assert.Single(root.Statements));

        Assert.NotNull(createIndex.WhereClause);
        var binary = Assert.IsType<BinaryExpression>(createIndex.WhereClause);
        var op = Assert.IsType<BuiltInOperator>(binary.Operator);
        Assert.Equal(PostgresBuiltInBinaryOperator.Equal, op.Operator);
        var left = Assert.IsType<ColumnReferenceExpression>(binary.Left);
        Assert.Equal("status", left.Identifier.Name);
        var right = Assert.IsType<LiteralExpression>(binary.Right);
        Assert.Equal("active", right.Value);
    }

    [Fact]
    public void FullIndex_HasNoWhereClause()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE INDEX idx_title ON film (title);
""";

        var root = parser.Parse(text);
        var createIndex = Assert.IsType<CreateIndexStatement>(Assert.Single(root.Statements));

        Assert.Null(createIndex.WhereClause);
    }
}
