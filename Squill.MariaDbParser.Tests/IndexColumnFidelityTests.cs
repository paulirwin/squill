using Squill.MariaDbParser.Syntax;
using Squill.TestFramework;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for the two index-key facets <c>MapIndexColumnNames</c> used to discard
/// (issue #161): a <b>prefix length</b> (<c>Brand(20)</c>) and an <b>expression key</b>
/// (<c>(a + b)</c>).
///
/// <para>
/// The grammar has always carried both — <c>indexColumnName : ((uid | STRING_LITERAL) ('('
/// decimalLiteral ')')? | expression) sortType?</c> — so this is entirely about what the mapper
/// reads back out. The shared helper feeds plain indexes, PRIMARY KEY, UNIQUE and FOREIGN KEY
/// alike, so each spelling is asserted through more than one of those paths.
/// </para>
/// </summary>
public class IndexColumnFidelityTests
{
    private static CreateIndexStatement ParseIndex(string text)
        => ParseAssertions.Single<CreateIndexStatement>(new AntlrMariaDbParser().Parse(text).Statements);

    private static CreateTableStatement ParseTable(string text)
        => ParseAssertions.Single<CreateTableStatement>(new AntlrMariaDbParser().Parse(text).Statements);

    // ---- Prefix lengths ----

    [Fact]
    public void CreateIndex_PrefixLength_IsCaptured()
    {
        var index = ParseIndex("CREATE INDEX IX_IceCreams_Brand ON IceCreams (Name, Brand(20));");

        Assert.Equal(2, index.Columns.Count);

        // The source declared Name in full and a 20-byte prefix of Brand.
        Assert.Equal("Name", index.Columns[0].Column?.Name);
        Assert.Null(index.Columns[0].PrefixLength);

        Assert.Equal("Brand", index.Columns[1].Column?.Name);
        Assert.Equal(20, index.Columns[1].PrefixLength);
    }

    [Fact]
    public void CreateIndex_PrefixLengthWithSortDirection_CapturesBoth()
    {
        var index = ParseIndex("CREATE INDEX ix ON t (a(10) DESC);");

        var column = Assert.Single(index.Columns);
        Assert.Equal("a", column.Column?.Name);
        Assert.Equal(10, column.PrefixLength);
        Assert.False(column.IsAscending);
    }

    [Fact]
    public void PrimaryKey_PrefixLength_IsCaptured()
    {
        var table = ParseTable("""
            CREATE TABLE IceCreams
            (
                Brand varchar(64) NOT NULL,
                Name  varchar(64) NOT NULL,
                PRIMARY KEY (Name, Brand(20))
            );
            """);

        var pk = Assert.Single(table.Elements.OfType<PrimaryKeyTableConstraint>());

        Assert.Equal(["Name", "Brand"], pk.Columns.Select(c => c.Column?.Name));
        Assert.Null(pk.Columns[0].PrefixLength);
        Assert.Equal(20, pk.Columns[1].PrefixLength);
    }

    [Fact]
    public void UniqueKey_PrefixLength_IsCaptured()
    {
        var table = ParseTable("""
            CREATE TABLE t
            (
                a varchar(64) NOT NULL,
                UNIQUE KEY uq_a (a(15))
            );
            """);

        var unique = Assert.Single(table.Elements.OfType<UniqueKeyTableConstraint>());

        var column = Assert.Single(unique.Columns);
        Assert.Equal("a", column.Column?.Name);
        Assert.Equal(15, column.PrefixLength);
    }

    [Fact]
    public void InlineIndex_PrefixLength_IsCaptured()
    {
        var table = ParseTable("""
            CREATE TABLE articles
            (
                article_id int NOT NULL,
                Body       text NOT NULL,
                KEY ix_body (Body(100))
            );
            """);

        var index = Assert.Single(table.Elements.OfType<IndexTableConstraint>());

        var column = Assert.Single(index.Columns);
        Assert.Equal("Body", column.Column?.Name);
        Assert.Equal(100, column.PrefixLength);
    }

    // A column with no prefix must stay null rather than defaulting to a number, so that the
    // model omits the facet entirely and hash-matches an extraction that read SUB_PART as NULL.
    [Fact]
    public void CreateIndex_WithoutPrefixLength_LeavesItNull()
    {
        var index = ParseIndex("CREATE INDEX ix ON t (a);");

        Assert.Null(Assert.Single(index.Columns).PrefixLength);
    }

    // ---- Expression keys ----

    [Fact]
    public void CreateIndex_ExpressionKey_IsCapturedRatherThanSkipped()
    {
        var index = ParseIndex("CREATE INDEX ix_totals_sum ON totals ((a + b), c);");

        // The whole point of #161: the declared index has TWO keys, and the expression one
        // comes first. Previously the expression key was silently dropped.
        Assert.Equal(2, index.Columns.Count);

        Assert.Null(index.Columns[0].Column);
        Assert.NotNull(index.Columns[0].KeyExpression);
        Assert.Contains("a", index.Columns[0].KeyExpression);
        Assert.Contains("b", index.Columns[0].KeyExpression);

        Assert.Equal("c", index.Columns[1].Column?.Name);
        Assert.Null(index.Columns[1].KeyExpression);
    }

    [Fact]
    public void CreateIndex_ExpressionKeyWithSortDirection_CapturesBoth()
    {
        var index = ParseIndex("CREATE INDEX ix ON t ((a + b) DESC);");

        var column = Assert.Single(index.Columns);
        Assert.NotNull(column.KeyExpression);
        Assert.False(column.IsAscending);
    }

    [Fact]
    public void InlineIndex_ExpressionKey_IsCaptured()
    {
        var table = ParseTable("""
            CREATE TABLE totals
            (
                a int NOT NULL,
                b int NOT NULL,
                KEY ix_sum ((a + b))
            );
            """);

        var index = Assert.Single(table.Elements.OfType<IndexTableConstraint>());

        var column = Assert.Single(index.Columns);
        Assert.Null(column.Column);
        Assert.NotNull(column.KeyExpression);
    }
}
