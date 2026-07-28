using Squill.MariaDbParser.Syntax;
using Squill.TestFramework;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for standalone CREATE INDEX statements, asserting the syntax tree the
/// mapper produces (issue #123). Indexes declared inline in a CREATE TABLE body are covered in
/// <see cref="CreateTableTests"/>; model-level concerns are covered in
/// Squill.Provider.MariaDb.Tests.
/// </summary>
public class CreateIndexTests
{
    private static CreateIndexStatement ParseOne(string text)
        => ParseAssertions.Single<CreateIndexStatement>(new AntlrMariaDbParser().Parse(text).Statements);

    [Fact]
    public void CreateIndex_CapturesNameTableAndColumn()
    {
        var index = ParseOne("CREATE INDEX idx_last_name ON actor (last_name);");

        Assert.Equal("idx_last_name", index.Name);
        Assert.Equal("actor", index.OnTable.Name);
        Assert.False(index.Unique);
        Assert.Null(index.IndexMethod);

        var column = Assert.Single(index.Columns);
        Assert.Equal("last_name", column.Column.Name);
        Assert.Null(column.IsAscending);
    }

    // FULLTEXT / SPATIAL occupy the same grammar slot as UNIQUE, and are captured as the index
    // kind rather than as an access method (issue #146).
    [Theory]
    [InlineData("CREATE FULLTEXT INDEX idx_t ON film_text (title);", "FULLTEXT")]
    [InlineData("CREATE SPATIAL INDEX idx_g ON geo (location);", "SPATIAL")]
    public void CreateIndex_SpecialKind_CapturesTheKind(string sql, string expectedKind)
    {
        var index = ParseOne(sql);

        Assert.Equal(expectedKind, index.IndexKind);
        Assert.False(index.Unique);
        Assert.Null(index.IndexMethod);
    }

    [Fact]
    public void CreateIndex_OrdinaryKind_HasNoIndexKind()
    {
        Assert.Null(ParseOne("CREATE INDEX idx_a ON actor (last_name);").IndexKind);
    }

    [Fact]
    public void CreateIndex_Unique_CapturesUniqueFlag()
    {
        var index = ParseOne("CREATE UNIQUE INDEX idx_email ON customer (email);");

        Assert.True(index.Unique);
        Assert.Equal("idx_email", index.Name);
    }

    // A database-qualified target table keeps both segments.
    [Fact]
    public void CreateIndex_QualifiedTable_CapturesBothSegments()
    {
        var index = ParseOne("CREATE INDEX idx_a ON sakila.actor (last_name);");

        Assert.Equal(new[] { "sakila", "actor" }, index.OnTable.Segments.Select(s => s.Name));
        Assert.Equal("actor", index.OnTable.Name);
    }

    [Fact]
    public void CreateIndex_BacktickIdentifiers_AreUnquoted()
    {
        var index = ParseOne("CREATE INDEX `idx name` ON `order` (`select`);");

        Assert.Equal("idx name", index.Name);
        Assert.Equal("order", index.OnTable.Name);
        Assert.Equal("select", Assert.Single(index.Columns).Column.Name);
    }

    // ---- Index method ----

    // `USING <method>` is captured upper-cased, matching how the catalog reports INDEX_TYPE.
    [Theory]
    [InlineData("BTREE", "BTREE")]
    [InlineData("btree", "BTREE")]
    [InlineData("HASH", "HASH")]
    [InlineData("hash", "HASH")]
    public void CreateIndex_UsingMethod_CapturesUpperCasedMethod(string declared, string expected)
    {
        var index = ParseOne($"CREATE INDEX idx_a ON t (a) USING {declared};");

        Assert.Equal(expected, index.IndexMethod);
    }

    // The method may also be written before the ON clause.
    [Fact]
    public void CreateIndex_UsingBeforeOnClause_CapturesMethod()
    {
        var index = ParseOne("CREATE INDEX idx_a USING BTREE ON t (a);");

        Assert.Equal("BTREE", index.IndexMethod);
        Assert.Equal("t", index.OnTable.Name);
        Assert.Equal("a", Assert.Single(index.Columns).Column.Name);
    }

    [Fact]
    public void CreateIndex_NoMethodWritten_HasNullMethod()
    {
        Assert.Null(ParseOne("CREATE INDEX idx_a ON t (a);").IndexMethod);
    }

    // ---- Sort direction ----

    // ASC/DESC map to a tri-state: true for ASC, false for DESC, and null when unwritten, so
    // the model builder can tell "explicitly ascending" from "unspecified".
    [Theory]
    [InlineData("a ASC", true)]
    [InlineData("a asc", true)]
    [InlineData("a DESC", false)]
    [InlineData("a desc", false)]
    [InlineData("a", null)]
    public void CreateIndex_CapturesSortDirection(string columnSpec, bool? expected)
    {
        var index = ParseOne($"CREATE INDEX idx_a ON t ({columnSpec});");

        Assert.Equal(expected, Assert.Single(index.Columns).IsAscending);
    }

    // ---- Multi-column indexes ----

    [Fact]
    public void CreateIndex_MultipleColumns_CapturesColumnsInOrder()
    {
        var index = ParseOne("CREATE INDEX idx_name ON actor (last_name, first_name);");

        Assert.Equal(
            new[] { "last_name", "first_name" },
            index.Columns.Select(c => c.Column.Name));
    }

    // Each column carries its own direction independently.
    [Fact]
    public void CreateIndex_MixedSortDirections_CapturesPerColumnDirection()
    {
        var index = ParseOne("CREATE INDEX idx_mixed ON t (a ASC, b DESC, c);");

        Assert.Collection(
            index.Columns,
            c => { Assert.Equal("a", c.Column.Name); Assert.True(c.IsAscending); },
            c => { Assert.Equal("b", c.Column.Name); Assert.False(c.IsAscending); },
            c => { Assert.Equal("c", c.Column.Name); Assert.Null(c.IsAscending); });
    }

    [Fact]
    public void CreateIndex_MultiColumnWithMethod_CapturesBoth()
    {
        var index = ParseOne("CREATE UNIQUE INDEX idx_a_b ON t (a, b DESC) USING BTREE;");

        Assert.True(index.Unique);
        Assert.Equal("BTREE", index.IndexMethod);
        Assert.Equal(new[] { "a", "b" }, index.Columns.Select(c => c.Column.Name));
        Assert.False(index.Columns[1].IsAscending);
    }

    // ---- Source position ----

    // The statement records where it starts, so build diagnostics can point back into the
    // source file (issue #53).
    [Fact]
    public void CreateIndex_RecordsSourcePosition()
    {
        var index = ParseOne(
            """
            CREATE INDEX idx_a ON t (a);
            """);

        Assert.Equal(1, index.Line);
        Assert.Equal(1, index.Column);
    }
}
