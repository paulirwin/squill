using Squill.PostgresParser.Syntax;
using Squill.TestFramework;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// <c>ALTER TABLE ... ADD CONSTRAINT c PRIMARY KEY USING INDEX ix</c> promotes an existing
/// unique index into a constraint rather than declaring its columns inline (issue #143). The
/// same spelling is accepted inside <c>CREATE TABLE</c> by the grammar.
///
/// It parses and carries the index name, but is not modeled: the constraint's columns live on
/// the referenced index, which is a separate object that may not even be declared in the
/// project — and Squill's declarative model has no way to express "this constraint is backed by
/// that specific index". The provider reports SQ1002 rather than throwing.
/// </summary>
public class CreateTableUsingIndexTests
{
    private static CreateTableStatement ParseOne(string text)
        => ParseAssertions.Single<CreateTableStatement>(new AntlrPostgresParser().Parse(text).Statements);

    [Fact]
    public void PrimaryKey_UsingIndex_CarriesTheIndexName()
    {
        var createTable = ParseOne("""
CREATE TABLE t
(
    id integer,
    CONSTRAINT pk_t PRIMARY KEY USING INDEX ix_t_id
);
""");

        var named = Assert.Single(createTable.Elements.OfType<NamedTableConstraint>());
        Assert.Equal("pk_t", named.Name.Name);

        var pk = Assert.IsType<PrimaryKeyTableConstraint>(named.Constraint);
        Assert.Equal("ix_t_id", pk.UsingIndex?.Name);

        // The columns are the index's, not the constraint's, so none are declared here.
        Assert.Empty(pk.Columns);
    }

    [Fact]
    public void Unique_UsingIndex_CarriesTheIndexName()
    {
        var createTable = ParseOne("""
CREATE TABLE t
(
    email text,
    UNIQUE USING INDEX ix_t_email
);
""");

        var unique = Assert.Single(createTable.Elements.OfType<UniqueTableConstraint>());

        Assert.Equal("ix_t_email", unique.UsingIndex?.Name);
        Assert.Empty(unique.Columns);
    }

    /// <summary>The ordinary parenthesized forms must keep declaring their columns and no index.</summary>
    [Fact]
    public void PrimaryKey_ColumnList_HasNoUsingIndex()
    {
        var createTable = ParseOne("CREATE TABLE t (a integer, b integer, PRIMARY KEY (a, b));");

        var pk = Assert.Single(createTable.Elements.OfType<PrimaryKeyTableConstraint>());

        Assert.Null(pk.UsingIndex);
        Assert.Equal(["a", "b"], pk.Columns.Select(i => i.Name));
    }

    [Fact]
    public void Unique_ColumnList_HasNoUsingIndex()
    {
        var createTable = ParseOne("CREATE TABLE t (a integer, UNIQUE (a));");

        var unique = Assert.Single(createTable.Elements.OfType<UniqueTableConstraint>());

        Assert.Null(unique.UsingIndex);
        Assert.Equal(["a"], unique.Columns.Select(i => i.Name));
    }
}
