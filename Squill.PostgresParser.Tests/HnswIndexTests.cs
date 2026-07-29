using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

public class HnswIndexTests
{
    [Fact]
    public void CreateIndex_Hnsw_ParsesOperatorClassAndWithOptions()
    {
        var parser = new AntlrPostgresParser();

        const string text =
            "CREATE INDEX ON items USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateIndexStatement>(Assert.Single(root.Statements));

        Assert.Equal("hnsw", stmt.UsingMethod?.Name);

        var element = Assert.Single(stmt.Elements);
        var columnRef = Assert.IsType<ColumnReferenceExpression>(element.Expression);
        Assert.Equal("embedding", columnRef.Identifier.Name);
        Assert.Equal("vector_cosine_ops", element.OperatorClass?.ToString());

        // WITH options are carried as ordered name/value pairs.
        Assert.Collection(stmt.WithOptions,
            o => { Assert.Equal("m", o.Name); Assert.Equal("16", o.Value); },
            o => { Assert.Equal("ef_construction", o.Name); Assert.Equal("64", o.Value); });
    }

    [Fact]
    public void CreateIndex_NoWithOptions_LeavesOptionsEmpty()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE INDEX ON items USING hnsw (embedding vector_l2_ops);";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateIndexStatement>(Assert.Single(root.Statements));

        var element = Assert.Single(stmt.Elements);
        Assert.Equal("vector_l2_ops", element.OperatorClass?.ToString());
        Assert.Empty(stmt.WithOptions);
    }
}
