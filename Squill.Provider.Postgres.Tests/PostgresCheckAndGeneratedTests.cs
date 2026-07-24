using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over the modeling and scripting of CHECK constraints and
/// generated (computed) columns (issue #120). Both were previously lost during the build —
/// a CHECK was dropped with only a warning, and a generated column threw. End-to-end
/// behavior against real Postgres is covered by the integration tests.
/// </summary>
public class PostgresCheckAndGeneratedTests
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()));

    private static async Task<SchemaComparison> CompareToEmptyAsync(string sql)
    {
        var provider = new PostgresDatabaseProvider("Host=unused");

        return SchemaCompare.Compare(provider, await BuildModelAsync(sql), new Model());
    }

    private static async Task<string> ScriptFromEmptyAsync(string sql)
        => new PostgresScriptGenerator().GenerateScript(await CompareToEmptyAsync(sql));

    // ---- CHECK: modeling ----

    [Fact]
    public async Task ColumnLevelCheck_BecomesCheckConstraintElement()
    {
        var model = await BuildModelAsync("""
CREATE TABLE product
(
    id integer PRIMARY KEY,
    price numeric CHECK (price > 0)
);
""");

        var check = Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlCheckConstraint);

        // Postgres names an unnamed inline CHECK <table>_<column>_check.
        Assert.Equal("product_price_check", SqlName.UnqualifiedOf((string)check.Name!));
    }

    [Fact]
    public async Task TableLevelCheck_UsesPostgresDerivedName()
    {
        var model = await BuildModelAsync("""
CREATE TABLE reservation
(
    id integer PRIMARY KEY,
    starts_at integer NOT NULL,
    ends_at integer NOT NULL,
    CHECK (ends_at > starts_at)
);
""");

        var check = Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlCheckConstraint);

        // An unnamed table-level CHECK is named <table>_check.
        Assert.Equal("reservation_check", SqlName.UnqualifiedOf((string)check.Name!));
    }

    [Fact]
    public async Task NamedCheck_KeepsItsName()
    {
        var model = await BuildModelAsync("""
CREATE TABLE product
(
    id integer PRIMARY KEY,
    price numeric,
    CONSTRAINT ck_price_positive CHECK (price > 0)
);
""");

        var check = Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlCheckConstraint);

        Assert.Equal("ck_price_positive", SqlName.UnqualifiedOf((string)check.Name!));
    }

    /// <summary>
    /// The predicate is carried for scripting but must not take part in the element's
    /// identity: Postgres rewrites it when it stores it, so a declared expression could
    /// never hash-match one read back from pg_get_constraintdef.
    /// </summary>
    [Fact]
    public async Task CheckExpression_DoesNotParticipateInIdentity()
    {
        var model = await BuildModelAsync("""
CREATE TABLE product
(
    id integer PRIMARY KEY,
    price numeric,
    CONSTRAINT ck_price CHECK (price > 0)
);
""");

        var check = Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlCheckConstraint);

        var property = Assert.Single(check.Properties,
            p => p.Name == PostgresPropertyNames.CheckExpression);

        Assert.False(property.ParticipatesInIdentity);
    }

    [Fact]
    public async Task MultipleChecksOnOneTable_AreAllModeled()
    {
        var model = await BuildModelAsync("""
CREATE TABLE product
(
    id integer PRIMARY KEY,
    price numeric CONSTRAINT ck_price CHECK (price > 0),
    stock integer,
    CONSTRAINT ck_stock CHECK (stock >= 0)
);
""");

        var checks = model.Elements
            .Where(e => e.Type == PostgresElementTypes.SqlCheckConstraint)
            .Select(e => SqlName.UnqualifiedOf((string)e.Name!))
            .ToList();

        Assert.Equal(["ck_price", "ck_stock"], checks);
    }

    // ---- CHECK: scripting ----

    [Fact]
    public async Task CheckConstraint_IsScriptedAsTableLevelClause()
    {
        var sql = await ScriptFromEmptyAsync("""
CREATE TABLE product
(
    id integer PRIMARY KEY,
    price numeric,
    CONSTRAINT ck_price CHECK (price > 0)
);
""");

        Assert.Contains("CONSTRAINT \"ck_price\" CHECK (\"price\" > 0)", sql);
    }

    /// <summary>
    /// A CHECK added to a table that already exists has no CREATE TABLE to carry the
    /// clause, so it must be scripted as its own ALTER TABLE ... ADD CONSTRAINT.
    /// </summary>
    [Fact]
    public async Task CheckAddedToExistingTable_IsScriptedAsAlterTable()
    {
        const string target = """
CREATE TABLE product
(
    id integer PRIMARY KEY,
    price numeric
);
""";

        const string source = """
CREATE TABLE product
(
    id integer PRIMARY KEY,
    price numeric,
    CONSTRAINT ck_price CHECK (price > 0)
);
""";

        var provider = new PostgresDatabaseProvider("Host=unused");

        var comparison = SchemaCompare.Compare(
            provider, await BuildModelAsync(source), await BuildModelAsync(target));

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains(
            "ALTER TABLE \"product\" ADD CONSTRAINT \"ck_price\" CHECK (\"price\" > 0);", sql);
    }

    // ---- Generated columns: modeling ----

    [Fact]
    public async Task GeneratedColumn_RecordsExpressionAndStorage()
    {
        var model = await BuildModelAsync("""
CREATE TABLE line_item
(
    id integer PRIMARY KEY,
    price numeric NOT NULL,
    quantity integer NOT NULL,
    total numeric GENERATED ALWAYS AS (price * quantity) STORED
);
""");

        var table = Assert.Single(model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var total = table.Relationships
            .Single(r => r.Name == PostgresRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == "total");

        Assert.True(total.GetProperty<bool?>(PostgresPropertyNames.IsStored));
        Assert.Equal("\"price\" * \"quantity\"",
            total.GetProperty<string>(PostgresPropertyNames.GeneratedExpression));
    }

    /// <summary>
    /// As with a CHECK predicate, Postgres rewrites a generation expression (adding casts
    /// and parentheses), so it cannot take part in identity. That the column is generated
    /// — IsStored — is a real structural difference and does participate.
    /// </summary>
    [Fact]
    public async Task GeneratedExpression_DoesNotParticipateInIdentity_ButIsStoredDoes()
    {
        var model = await BuildModelAsync("""
CREATE TABLE line_item
(
    id integer PRIMARY KEY,
    price numeric NOT NULL,
    quantity integer NOT NULL,
    total numeric GENERATED ALWAYS AS (price * quantity) STORED
);
""");

        var table = Assert.Single(model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var total = table.Relationships
            .Single(r => r.Name == PostgresRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == "total");

        Assert.False(Assert.Single(total.Properties,
            p => p.Name == PostgresPropertyNames.GeneratedExpression).ParticipatesInIdentity);

        Assert.True(Assert.Single(total.Properties,
            p => p.Name == PostgresPropertyNames.IsStored).ParticipatesInIdentity);
    }

    /// <summary>
    /// GENERATED ... AS IDENTITY shares the GENERATED keyword but is an entirely different
    /// feature; it must still be modeled as an identity column, not a generated one.
    /// </summary>
    [Fact]
    public async Task IdentityColumn_IsNotModeledAsGenerated()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY);");

        var table = Assert.Single(model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var id = table.Relationships
            .Single(r => r.Name == PostgresRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single();

        Assert.True(id.GetProperty<bool?>(PostgresPropertyNames.IsIdentity));
        Assert.Null(id.GetProperty<string>(PostgresPropertyNames.GeneratedExpression));
    }

    // ---- Generated columns: scripting ----

    [Fact]
    public async Task GeneratedColumn_IsScriptedWithGenerationClause()
    {
        var sql = await ScriptFromEmptyAsync("""
CREATE TABLE line_item
(
    id integer PRIMARY KEY,
    price numeric NOT NULL,
    quantity integer NOT NULL,
    total numeric GENERATED ALWAYS AS (price * quantity) STORED
);
""");

        Assert.Contains(
            "\"total\" numeric GENERATED ALWAYS AS (\"price\" * \"quantity\") STORED", sql);
    }

    /// <summary>
    /// A generated column takes neither a DEFAULT nor an explicit NULL — Postgres rejects
    /// both — so the usual nullability suffix must be suppressed.
    /// </summary>
    [Fact]
    public async Task GeneratedColumn_IsNotScriptedWithNullSuffix()
    {
        var sql = await ScriptFromEmptyAsync("""
CREATE TABLE line_item
(
    id integer PRIMARY KEY,
    price numeric NOT NULL,
    total numeric GENERATED ALWAYS AS (price * 2) STORED
);
""");

        Assert.DoesNotContain("STORED NULL", sql);
        Assert.DoesNotContain("(\"price\" * 2) STORED NULL", sql);
    }

    [Fact]
    public async Task NotNullGeneratedColumn_KeepsNotNull()
    {
        var sql = await ScriptFromEmptyAsync("""
CREATE TABLE line_item
(
    id integer PRIMARY KEY,
    price numeric NOT NULL,
    total numeric NOT NULL GENERATED ALWAYS AS (price * 2) STORED
);
""");

        Assert.Contains("\"total\" numeric NOT NULL GENERATED ALWAYS AS (\"price\" * 2) STORED", sql);
    }

    /// <summary>
    /// String concatenation is a common generation expression; it goes through the general
    /// operator path in the parser rather than one of the fixed math operators.
    /// </summary>
    [Fact]
    public async Task GeneratedColumn_WithConcatenationOperator_RoundTripsToScript()
    {
        var sql = await ScriptFromEmptyAsync("""
CREATE TABLE person
(
    id integer PRIMARY KEY,
    first_name text NOT NULL,
    last_name text NOT NULL,
    full_name text GENERATED ALWAYS AS (first_name || ' ' || last_name) STORED
);
""");

        Assert.Contains(
            "GENERATED ALWAYS AS (\"first_name\" || ' ' || \"last_name\") STORED", sql);
    }
}
