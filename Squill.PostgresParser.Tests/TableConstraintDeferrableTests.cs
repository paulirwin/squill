using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Table-level constraint attributes (DEFERRABLE / INITIALLY DEFERRED), issue #160 — the
/// counterpart to the inline spelling covered by <see cref="ColumnCollateAndDeferrableTests"/>.
///
/// The two spellings reach the parser by different routes: inline, each attribute is a
/// <c>colconstraint</c> alternative arriving as its own sibling node, whereas at table level
/// every <c>constraintelem</c> alternative ends in a single <c>constraintattributespec</c>
/// holding a list of <c>constraintattributeElem</c>. That list was never read, so the clause
/// parsed and was discarded — the asymmetry #160 calls out, where the inline form refused to
/// build while the table-level form silently lied.
/// </summary>
public class TableConstraintDeferrableTests
{
    [Fact]
    public void TableLevelDeferrableForeignKey_IsParsed()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    id  integer PRIMARY KEY,
    cid integer,
    CONSTRAINT fk_orders FOREIGN KEY (cid) REFERENCES customers (id) DEFERRABLE INITIALLY DEFERRED
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var named = Assert.Single(createTable.Elements.OfType<NamedTableConstraint>());

        Assert.Equal("fk_orders", named.Name.Name);

        var fk = Assert.IsType<ForeignKeyTableConstraint>(named.Constraint);

        // Unlike the inline form, the whole spec is one node, so both facets land together.
        Assert.True(fk.IsDeferrable);
        Assert.True(fk.IsInitiallyDeferred);
    }

    [Fact]
    public void TableLevelForeignKeyWithoutAttributes_IsNotDeferrable()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    cid integer,
    CONSTRAINT fk_orders FOREIGN KEY (cid) REFERENCES customers (id)
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var named = Assert.Single(createTable.Elements.OfType<NamedTableConstraint>());
        var fk = Assert.IsType<ForeignKeyTableConstraint>(named.Constraint);

        // constraintattributespec matches empty, so an absent clause is a present-but-childless
        // context rather than a null one. Either way the default is NOT DEFERRABLE.
        Assert.False(fk.IsDeferrable);
        Assert.False(fk.IsInitiallyDeferred);
    }

    [Fact]
    public void TableLevelDeferrable_WithoutInitiallyClause_IsDeferrableButNotDeferred()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    cid integer,
    CONSTRAINT fk_orders FOREIGN KEY (cid) REFERENCES customers (id) DEFERRABLE
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var fk = Assert.IsType<ForeignKeyTableConstraint>(
            Assert.Single(createTable.Elements.OfType<NamedTableConstraint>()).Constraint);

        Assert.True(fk.IsDeferrable);
        Assert.False(fk.IsInitiallyDeferred);
    }

    [Fact]
    public void TableLevelNotDeferrableInitiallyImmediate_IsParsed()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    cid integer,
    CONSTRAINT fk_orders FOREIGN KEY (cid) REFERENCES customers (id)
        NOT DEFERRABLE INITIALLY IMMEDIATE
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var fk = Assert.IsType<ForeignKeyTableConstraint>(
            Assert.Single(createTable.Elements.OfType<NamedTableConstraint>()).Constraint);

        // The explicit spelling of the default.
        Assert.False(fk.IsDeferrable);
        Assert.False(fk.IsInitiallyDeferred);
    }

    /// <summary>
    /// INITIALLY DEFERRED alone implies DEFERRABLE: PostgreSQL rejects pairing it with NOT
    /// DEFERRABLE, and the catalog reports condeferrable = true. Resolved in the visitor so
    /// both the inline and table-level paths present the same already-collapsed answer.
    /// </summary>
    [Fact]
    public void TableLevelInitiallyDeferredAlone_ImpliesDeferrable()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    cid integer,
    CONSTRAINT fk_orders FOREIGN KEY (cid) REFERENCES customers (id) INITIALLY DEFERRED
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var fk = Assert.IsType<ForeignKeyTableConstraint>(
            Assert.Single(createTable.Elements.OfType<NamedTableConstraint>()).Constraint);

        Assert.True(fk.IsDeferrable);
        Assert.True(fk.IsInitiallyDeferred);
    }

    [Fact]
    public void TableLevelDeferrablePrimaryKey_IsParsed()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE t
(
    id integer,
    CONSTRAINT pk_t PRIMARY KEY (id) DEFERRABLE INITIALLY DEFERRED
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var pk = Assert.IsType<PrimaryKeyTableConstraint>(
            Assert.Single(createTable.Elements.OfType<NamedTableConstraint>()).Constraint);

        Assert.True(pk.IsDeferrable);
        Assert.True(pk.IsInitiallyDeferred);
    }

    [Fact]
    public void TableLevelDeferrableUnique_IsParsed()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE t
(
    email text,
    CONSTRAINT uq_t UNIQUE (email) DEFERRABLE INITIALLY DEFERRED
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var unique = Assert.IsType<UniqueTableConstraint>(
            Assert.Single(createTable.Elements.OfType<NamedTableConstraint>()).Constraint);

        Assert.True(unique.IsDeferrable);
        Assert.True(unique.IsInitiallyDeferred);
    }

    /// <summary>
    /// NOT VALID and NO INHERIT share the constraintattributeElem rule with the deferrability
    /// keywords but mean something else entirely. Neither is modeled, so neither may be read as
    /// a deferrability signal — in particular the NOT of NOT VALID must not be mistaken for the
    /// NOT of NOT DEFERRABLE.
    /// </summary>
    [Fact]
    public void NotValid_IsNotMistakenForNotDeferrable()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE t
(
    cid integer,
    CONSTRAINT fk_t FOREIGN KEY (cid) REFERENCES customers (id) DEFERRABLE NOT VALID
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var fk = Assert.IsType<ForeignKeyTableConstraint>(
            Assert.Single(createTable.Elements.OfType<NamedTableConstraint>()).Constraint);

        Assert.True(fk.IsDeferrable);
    }
}
