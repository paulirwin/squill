using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// The index-shaped clauses a PRIMARY KEY or UNIQUE table constraint accepts (issue #210):
/// <c>INCLUDE (...)</c>, <c>WITH (...)</c> storage parameters, and
/// <c>USING INDEX TABLESPACE</c>. All three already work on <c>CREATE INDEX</c>, and all three
/// parsed here and were then discarded, so the same declaration behaved differently depending
/// on which spelling the author chose.
///
/// <c>NULLS NOT DISTINCT</c> is deliberately absent: unlike these three it is not merely
/// unread, it does not parse at all, because the vendored grammar threads
/// <c>nulls_distinct</c> into <c>indexstmt</c> only and not into <c>constraintelem</c>. That
/// is a grammar gap rather than a visitor gap and is tracked by issue #187.
/// </summary>
public class ConstraintIndexOptionTests
{
    private static T ParseConstraint<T>(string text) where T : TableConstraint
    {
        var root = new AntlrPostgresParser().Parse(text);
        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        return Assert.Single(createTable.Elements
            .Select(e => e is NamedTableConstraint named ? named.Constraint : e)
            .OfType<T>());
    }

    [Fact]
    public void Unique_Include_IsParsed()
    {
        var unique = ParseConstraint<UniqueTableConstraint>(
            "CREATE TABLE t (a integer, b integer, UNIQUE (a) INCLUDE (b));");

        Assert.Equal(["a"], unique.Columns.Select(c => c.Name));
        Assert.Equal(["b"], unique.IncludeColumns.Select(c => c.Name));
    }

    [Fact]
    public void PrimaryKey_Include_IsParsed()
    {
        var pk = ParseConstraint<PrimaryKeyTableConstraint>(
            "CREATE TABLE t (a integer, b integer, c integer, PRIMARY KEY (a) INCLUDE (b, c));");

        Assert.Equal(["a"], pk.Columns.Select(c => c.Name));
        Assert.Equal(["b", "c"], pk.IncludeColumns.Select(c => c.Name));
    }

    [Fact]
    public void Unique_WithStorageParameters_AreParsed()
    {
        var unique = ParseConstraint<UniqueTableConstraint>(
            "CREATE TABLE t (a integer, UNIQUE (a) WITH (fillfactor = 70));");

        var option = Assert.Single(unique.WithOptions);

        Assert.Equal("fillfactor", option.Name);
        Assert.Equal("70", option.Value);
    }

    [Fact]
    public void Unique_MultipleStorageParameters_KeepOrder()
    {
        var unique = ParseConstraint<UniqueTableConstraint>(
            "CREATE TABLE t (a integer, UNIQUE (a) WITH (fillfactor = 70, deduplicate_items = off));");

        Assert.Equal(["fillfactor", "deduplicate_items"], unique.WithOptions.Select(o => o.Name));
        Assert.Equal(["70", "off"], unique.WithOptions.Select(o => o.Value));
    }

    [Fact]
    public void Unique_UsingIndexTablespace_IsParsed()
    {
        var unique = ParseConstraint<UniqueTableConstraint>(
            "CREATE TABLE t (a integer, UNIQUE (a) USING INDEX TABLESPACE fast_ssd);");

        Assert.Equal("fast_ssd", unique.TableSpace?.Name);
    }

    [Fact]
    public void PrimaryKey_UsingIndexTablespace_IsParsed()
    {
        var pk = ParseConstraint<PrimaryKeyTableConstraint>(
            "CREATE TABLE t (a integer, PRIMARY KEY (a) USING INDEX TABLESPACE fast_ssd);");

        Assert.Equal("fast_ssd", pk.TableSpace?.Name);
    }

    /// <summary>All three clauses may appear together, in the grammar's order.</summary>
    [Fact]
    public void Unique_AllThreeClauses_AreParsedTogether()
    {
        var unique = ParseConstraint<UniqueTableConstraint>("""
CREATE TABLE t
(
    a integer,
    b integer,
    UNIQUE (a) INCLUDE (b) WITH (fillfactor = 70) USING INDEX TABLESPACE fast_ssd
);
""");

        Assert.Equal(["b"], unique.IncludeColumns.Select(c => c.Name));
        Assert.Equal("fillfactor", Assert.Single(unique.WithOptions).Name);
        Assert.Equal("fast_ssd", unique.TableSpace?.Name);
    }

    /// <summary>
    /// A constraint declaring none of them carries nothing, so an ordinary PRIMARY KEY or
    /// UNIQUE is unchanged and cannot re-diff.
    /// </summary>
    [Fact]
    public void OrdinaryConstraints_CarryNoIndexOptions()
    {
        var unique = ParseConstraint<UniqueTableConstraint>(
            "CREATE TABLE t (a integer, UNIQUE (a));");

        Assert.Empty(unique.IncludeColumns);
        Assert.Empty(unique.WithOptions);
        Assert.Null(unique.TableSpace);

        var pk = ParseConstraint<PrimaryKeyTableConstraint>(
            "CREATE TABLE t (a integer, PRIMARY KEY (a));");

        Assert.Empty(pk.IncludeColumns);
        Assert.Empty(pk.WithOptions);
        Assert.Null(pk.TableSpace);
    }

    /// <summary>
    /// The clauses coexist with the DEFERRABLE spec every constraintelem alternative ends in
    /// (issue #160), which follows them in the grammar.
    /// </summary>
    [Fact]
    public void Unique_IncludeAndDeferrable_BothSurvive()
    {
        var unique = ParseConstraint<UniqueTableConstraint>("""
CREATE TABLE t
(
    a integer,
    b integer,
    UNIQUE (a) INCLUDE (b) DEFERRABLE INITIALLY DEFERRED
);
""");

        Assert.Equal(["b"], unique.IncludeColumns.Select(c => c.Name));
        Assert.True(unique.IsDeferrable);
        Assert.True(unique.IsInitiallyDeferred);
    }
}
