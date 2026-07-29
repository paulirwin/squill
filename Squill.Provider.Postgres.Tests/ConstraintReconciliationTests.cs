using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// A primary key or foreign key declared on a table that ALREADY exists must be reconciled on
/// its own (issue #157). Both are dependent elements, so <see cref="SchemaCompare"/>'s main loop
/// skips them, and neither changes its table's hash — before the fix nothing was left to notice
/// the change, and the deploy was a silent no-op.
///
/// These run entirely on parsed models with no database, so they pin the DIFF shape; the
/// end-to-end behaviour against real PostgreSQL is covered by the integration tests.
/// </summary>
public class ConstraintReconciliationTests
{
    private static Task<Model> ParseAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql, ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()));

    private static async Task<SchemaComparison> CompareAsync(
        string from, string to, DeployOptions? options = null)
    {
        var a = await ParseAsync(from);
        var b = await ParseAsync(to);

        Assert.False(
            HashUtility.HashesEqual(a.Hash, b.Hash),
            "The two schemas parse to the same model, so this test proves nothing.");

        return SchemaCompare.Compare(new PostgresDatabaseProvider("Host=unused"), b, a, options);
    }

    private static string Script(SchemaComparison comparison)
        => new PostgresScriptGenerator().GenerateScript(comparison);

    private const string TwoTables = """
CREATE TABLE customers (id integer PRIMARY KEY);
CREATE TABLE orders (id integer PRIMARY KEY, customer_id integer NOT NULL);
""";

    private const string TwoTablesWithForeignKey = """
CREATE TABLE customers (id integer PRIMARY KEY);
CREATE TABLE orders (id integer PRIMARY KEY, customer_id integer NOT NULL,
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id));
""";

    [Fact]
    public async Task AddForeignKeyToExistingTables_ProducesCreateDelta()
    {
        var comparison = await CompareAsync(TwoTables, TwoTablesWithForeignKey);

        var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(PostgresElementTypes.SqlForeignKeyConstraint, create.Element.Type);

        var sql = Script(comparison);
        Assert.Contains("ADD CONSTRAINT \"fk_orders_customer\"", sql);
        Assert.Contains("REFERENCES", sql);
    }

    [Fact]
    public async Task DropForeignKey_ProducesDropDelta_OnlyUnderDropObjectsNotInSource()
    {
        var kept = await CompareAsync(TwoTablesWithForeignKey, TwoTables);
        Assert.Empty(kept.Deltas);

        var comparison = await CompareAsync(
            TwoTablesWithForeignKey, TwoTables, new DeployOptions { DropObjectsNotInSource = true });

        var drop = Assert.IsType<DropDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(PostgresElementTypes.SqlForeignKeyConstraint, drop.Element.Type);
        Assert.False(drop.CausesDataLoss);

        Assert.Contains("DROP CONSTRAINT IF EXISTS \"fk_orders_customer\";", Script(comparison));
    }

    /// <summary>
    /// Moving the PRIMARY KEY to another column under the same constraint name is a
    /// redefinition, so it must be a drop-and-recreate rather than a create or a drop.
    /// </summary>
    [Fact]
    public async Task MovePrimaryKey_ProducesRecreateDelta()
    {
        var comparison = await CompareAsync(
            """
CREATE TABLE table1 (id integer NOT NULL, alternate_pk integer NOT NULL,
    CONSTRAINT pk_table1 PRIMARY KEY (id));
""",
            """
CREATE TABLE table1 (id integer NOT NULL, alternate_pk integer NOT NULL,
    CONSTRAINT pk_table1 PRIMARY KEY (alternate_pk));
""");

        var recreate = Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(PostgresElementTypes.SqlPrimaryKeyConstraint, recreate.SourceElement.Type);

        var sql = Script(comparison);

        // The old key must be dropped before the new one is added: a table can only have one.
        var drop = sql.IndexOf("DROP CONSTRAINT", StringComparison.Ordinal);
        var add = sql.IndexOf("ADD CONSTRAINT", StringComparison.Ordinal);

        Assert.True(drop >= 0, $"Expected the old primary key to be dropped in:\n{sql}");
        Assert.True(add > drop, $"Expected the new primary key added after the drop in:\n{sql}");
        Assert.Contains("\"alternate_pk\"", sql);
    }

    [Fact]
    public async Task AddPrimaryKeyToExistingTable_ProducesCreateDelta()
    {
        var comparison = await CompareAsync(
            "CREATE TABLE table1 (id integer NOT NULL, other integer NOT NULL);",
            """
CREATE TABLE table1 (id integer NOT NULL, other integer NOT NULL,
    CONSTRAINT pk_table1 PRIMARY KEY (id));
""");

        var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(PostgresElementTypes.SqlPrimaryKeyConstraint, create.Element.Type);

        Assert.Contains("ADD CONSTRAINT \"pk_table1\" PRIMARY KEY (\"id\");", Script(comparison));
    }

    /// <summary>
    /// A constraint on a table that is itself being dropped needs no drop of its own: the
    /// table's DROP takes it along. Now that every constraint is reconciled standalone, the
    /// drop pass has to recognise that case or it would emit a redundant DROP CONSTRAINT
    /// against a table that is about to disappear.
    /// </summary>
    [Fact]
    public async Task ConstraintsOnDroppedTables_AreNotAlsoDroppedStandalone()
    {
        var comparison = await CompareAsync(
            TwoTablesWithForeignKey,
            "CREATE TABLE customers (id integer PRIMARY KEY);",
            new DeployOptions { DropObjectsNotInSource = true, BlockOnPossibleDataLoss = false });

        // Just the table: its primary and foreign keys go with it.
        var drop = Assert.IsType<DropDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(PostgresElementTypes.SqlTable, drop.Element.Type);
        Assert.Equal("orders", drop.Element.Name);

        Assert.DoesNotContain("DROP CONSTRAINT", Script(comparison));
    }

    /// <summary>
    /// The same, for a table outside the default schema. A table element carries a qualified
    /// name while a constraint records its table by bare reference, so matching a dependent to
    /// the table being dropped has to reconcile the two spellings — otherwise a non-public
    /// table's constraints would each get a redundant drop of their own.
    /// </summary>
    [Fact]
    public async Task ConstraintsOnDroppedTables_AreNotAlsoDroppedStandalone_InANonPublicSchema()
    {
        var comparison = await CompareAsync(
            """
CREATE SCHEMA staging;
CREATE TABLE staging.customers (id integer PRIMARY KEY);
CREATE TABLE staging.orders (id integer PRIMARY KEY, customer_id integer NOT NULL,
    CONSTRAINT fk_staging_orders FOREIGN KEY (customer_id) REFERENCES staging.customers (id));
""",
            """
CREATE SCHEMA staging;
CREATE TABLE staging.customers (id integer PRIMARY KEY);
""",
            new DeployOptions { DropObjectsNotInSource = true, BlockOnPossibleDataLoss = false });

        var drop = Assert.IsType<DropDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(PostgresElementTypes.SqlTable, drop.Element.Type);

        Assert.DoesNotContain("DROP CONSTRAINT", Script(comparison));
    }

    /// <summary>
    /// The regression guard for the deferred-FK machinery: when the tables themselves are being
    /// created, their constraints ride along on the CREATE TABLE. Making an FK reconcilable
    /// standalone must not also produce a second, duplicate CreateDelta for it.
    /// </summary>
    [Fact]
    public async Task ConstraintsOnNewTables_AreNotAlsoCreatedStandalone()
    {
        var model = await ParseAsync(TwoTablesWithForeignKey);
        var comparison = SchemaCompare.Compare(
            new PostgresDatabaseProvider("Host=unused"), model, new Model());

        Assert.Equal(2, comparison.Deltas.Count);
        Assert.All(comparison.Deltas, delta =>
            Assert.Equal(PostgresElementTypes.SqlTable, Assert.IsType<CreateDelta>(delta).Element.Type));

        var sql = Script(comparison);
        Assert.Equal(2, sql.Split("CREATE TABLE").Length - 1);
        Assert.DoesNotContain("ALTER TABLE", sql);
    }

    /// <summary>
    /// The same guard for a circular reference, where the cycle-breaking constraint IS removed
    /// from its table's dependents and deferred. That removal must not make the standalone pass
    /// think the constraint is unaccounted for and emit a duplicate.
    /// </summary>
    [Fact]
    public async Task DeferredCycleConstraint_IsNotAlsoCreatedStandalone()
    {
        var model = await ParseAsync("""
CREATE TABLE husband (id integer PRIMARY KEY, wife_id integer NULL,
    CONSTRAINT fk_husband_wife FOREIGN KEY (wife_id) REFERENCES wife (id));
CREATE TABLE wife (id integer PRIMARY KEY, husband_id integer NULL,
    CONSTRAINT fk_wife_husband FOREIGN KEY (husband_id) REFERENCES husband (id));
""");

        var comparison = SchemaCompare.Compare(
            new PostgresDatabaseProvider("Host=unused"), model, new Model());

        var sql = Script(comparison);

        Assert.Equal(1, sql.Split("ADD CONSTRAINT").Length - 1);
        Assert.Equal(2, sql.Split("CREATE TABLE").Length - 1);
    }
}
