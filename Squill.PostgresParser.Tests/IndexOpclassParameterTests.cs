using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Operator class parameters (PostgreSQL 13+), the second alternative of
/// <c>index_elem_options</c> (issue #211).
///
/// The visitor only ever read the first alternative, so an index key written with parameters
/// lost not just the parameters but the operator class name itself: both live on the alternative
/// that was never visited.
/// </summary>
public class IndexOpclassParameterTests
{
    private static IndexElement ParseSingleElement(string text)
    {
        var parser = new AntlrPostgresParser();
        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateIndexStatement>(Assert.Single(root.Statements));
        return Assert.Single(stmt.Elements);
    }

    [Fact]
    public void OpclassParameters_KeepTheOperatorClassName()
    {
        // Measured: PostgreSQL rejects `(tsv (siglen=256))` outright ("column siglen does not
        // exist"), so the class name is not optional decoration here: it is required for the
        // DDL to parse at all, and losing it made the emitted index undeployable.
        var element = ParseSingleElement(
            "CREATE INDEX ix ON docs USING gist (tsv tsvector_ops(siglen=256));");

        Assert.Equal("tsvector_ops", element.OperatorClass?.ToString());
    }

    [Fact]
    public void OpclassParameters_AreCapturedAsNameValuePairs()
    {
        var element = ParseSingleElement(
            "CREATE INDEX ix ON docs USING gist (tsv tsvector_ops(siglen=256));");

        var option = Assert.Single(element.OperatorClassParameters);
        Assert.Equal("siglen", option.Name);
        Assert.Equal("256", option.Value);
    }

    [Fact]
    public void OpclassParameters_KeepEveryPairInOrder()
    {
        var element = ParseSingleElement(
            "CREATE INDEX ix ON docs USING gist (tsv tsvector_ops(siglen=256, other=4));");

        Assert.Collection(element.OperatorClassParameters,
            o => { Assert.Equal("siglen", o.Name); Assert.Equal("256", o.Value); },
            o => { Assert.Equal("other", o.Name); Assert.Equal("4", o.Value); });
    }

    [Fact]
    public void OpclassParameters_CoexistWithOrderingAndCollate()
    {
        // The parameterized alternative carries its own asc_desc_ and nulls_order_, so those
        // clauses have to be read from it rather than from the first alternative.
        var element = ParseSingleElement(
            """
            CREATE INDEX ix ON docs USING btree
                (body COLLATE "C" text_ops(x=1) DESC NULLS FIRST);
            """);

        Assert.Equal("text_ops", element.OperatorClass?.ToString());
        Assert.Equal("C", element.Collation?.ToString());
        Assert.Equal(IndexElementDirection.Desc, element.Direction);
        Assert.Equal(IndexElementNullOrder.NullsFirst, element.NullOrder);
        Assert.Equal("x", Assert.Single(element.OperatorClassParameters).Name);
    }

    [Fact]
    public void SchemaQualifiedOpclass_WithParameters_KeepsBothParts()
    {
        var element = ParseSingleElement(
            "CREATE INDEX ix ON docs USING gist (tsv pg_catalog.tsvector_ops(siglen=256));");

        Assert.Equal("pg_catalog.tsvector_ops", element.OperatorClass?.ToString());
        Assert.Equal("siglen", Assert.Single(element.OperatorClassParameters).Name);
    }

    [Fact]
    public void PlainOpclass_HasNoParameters()
    {
        var element = ParseSingleElement(
            "CREATE INDEX ix ON docs USING gist (tsv tsvector_ops);");

        Assert.Equal("tsvector_ops", element.OperatorClass?.ToString());
        Assert.Empty(element.OperatorClassParameters);
    }

    [Fact]
    public void NoOpclass_HasNeitherClassNorParameters()
    {
        var element = ParseSingleElement("CREATE INDEX ix ON docs USING gist (tsv);");

        Assert.Null(element.OperatorClass);
        Assert.Empty(element.OperatorClassParameters);
    }
}
