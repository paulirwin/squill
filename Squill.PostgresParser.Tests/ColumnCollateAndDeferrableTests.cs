using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Column-level COLLATE and inline constraint attributes (DEFERRABLE / INITIALLY DEFERRED),
/// issue #159. Both are <c>colconstraint</c> alternatives that are neither a
/// <c>CONSTRAINT name ...</c> wrapper nor a <c>colconstraintelem</c>, so both used to reach the
/// same else-branch in <c>AddColumnConstraints</c> and throw.
/// </summary>
public class ColumnCollateAndDeferrableTests
{
    [Fact]
    public void ColumnCollate_IsParsed()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE people
(
    id  integer PRIMARY KEY,
    ssn character varying(11) COLLATE "POSIX" NOT NULL
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var ssn = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);

        Assert.Equal("ssn", ssn.Name.Name);

        var collate = Assert.Single(ssn.Constraints.OfType<CollateColumnConstraint>());

        // The collation name is case-sensitive and conventionally quoted, so the quotes are
        // stripped but the case is kept — matching how a range type's COLLATION is parsed.
        Assert.Equal("POSIX", collate.Collation.ToString());

        // COLLATE does not displace the other constraints on the column.
        Assert.Single(ssn.Constraints.OfType<NullableColumnConstraint>());
    }

    [Fact]
    public void ColumnCollate_SchemaQualifiedName_IsParsed()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE people (name text COLLATE public."POSIX");
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var name = Assert.IsType<ColumnDefinition>(createTable.Elements[0]);
        var collate = Assert.Single(name.Constraints.OfType<CollateColumnConstraint>());

        Assert.Equal("public.POSIX", collate.Collation.ToString());
    }

    [Fact]
    public void InlineDeferrableForeignKey_IsParsed()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    id  integer PRIMARY KEY,
    cid integer REFERENCES customers (id) DEFERRABLE INITIALLY DEFERRED
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var cid = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);

        Assert.Single(cid.Constraints.OfType<ForeignKeyColumnConstraint>());

        // DEFERRABLE and INITIALLY DEFERRED are two separate colconstraint alternatives, so
        // each arrives as its own constraint node.
        var attributes = cid.Constraints.OfType<ConstraintAttributeColumnConstraint>().ToList();

        Assert.Equal(2, attributes.Count);
        Assert.Contains(attributes, a => a is { Deferrable: true, InitiallyDeferred: null });
        Assert.Contains(attributes, a => a is { Deferrable: null, InitiallyDeferred: true });
    }

    [Fact]
    public void NotDeferrableAndInitiallyImmediate_AreParsed()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    cid integer REFERENCES customers (id) NOT DEFERRABLE INITIALLY IMMEDIATE
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var cid = Assert.IsType<ColumnDefinition>(createTable.Elements[0]);

        var attributes = cid.Constraints.OfType<ConstraintAttributeColumnConstraint>().ToList();

        Assert.Equal(2, attributes.Count);
        Assert.Contains(attributes, a => a is { Deferrable: false, InitiallyDeferred: null });
        Assert.Contains(attributes, a => a is { Deferrable: null, InitiallyDeferred: false });
    }
}
