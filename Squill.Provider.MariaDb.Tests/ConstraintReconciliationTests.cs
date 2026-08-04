using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// A primary key or foreign key declared on a table that ALREADY exists must be reconciled on
/// its own (issue #157). Both are dependent elements, so <see cref="SchemaCompare"/>'s main loop
/// skips them, and neither changes its table's hash — before the fix nothing was left to notice
/// the change, and the deploy was a silent no-op.
///
/// These run entirely on parsed models with no database, so they pin the DIFF shape; the
/// end-to-end behaviour against a real engine is covered by the integration tests.
/// </summary>
public class ConstraintReconciliationTests
{
    private static async Task<Model> ParseAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(
                workspace, new AntlrMariaDbParser(), new MariaDb12DatabaseSchemaProvider())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;
    }

    // Compares schema B against schema A as if A were already deployed. Both are parsed
    // models, which is enough to pin the delta shape: the catalog-extracted form of A differs
    // only in spellings the normalizers already reconcile.
    private static async Task<SchemaComparison> CompareAsync(
        string from, string to, DeployOptions? options = null)
    {
        var a = await ParseAsync(from);
        var b = await ParseAsync(to);

        Assert.False(
            HashUtility.HashesEqual(a.Hash, b.Hash),
            "The two schemas parse to the same model, so this test proves nothing.");

        return SchemaCompare.Compare(new MariaDbDatabaseProvider("Server=unused"), b, a, options);
    }

    private static string Script(SchemaComparison comparison)
        => new MariaDbScriptGenerator().GenerateScript(comparison);

    private const string TwoTables = """
        CREATE TABLE Customers (Id int NOT NULL PRIMARY KEY);
        CREATE TABLE Orders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL);
        """;

    private const string TwoTablesWithForeignKey = """
        CREATE TABLE Customers (Id int NOT NULL PRIMARY KEY);
        CREATE TABLE Orders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL,
            CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES Customers (Id));
        """;

    [Fact]
    public async Task AddForeignKeyToExistingTables_ProducesCreateDelta()
    {
        var comparison = await CompareAsync(TwoTables, TwoTablesWithForeignKey);

        var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(MariaDbElementTypes.SqlForeignKeyConstraint, create.Element.Type);

        var sql = Script(comparison);
        Assert.Contains("ALTER TABLE `Orders` ADD CONSTRAINT `FK_Orders_Customers`", sql);
        Assert.Contains("REFERENCES `Customers`", sql);
    }

    [Fact]
    public async Task DropForeignKey_ProducesDropDelta_OnlyUnderDropObjectsNotInSource()
    {
        var kept = await CompareAsync(TwoTablesWithForeignKey, TwoTables);
        Assert.Empty(kept.Deltas);

        var comparison = await CompareAsync(
            TwoTablesWithForeignKey, TwoTables, new DeployOptions { DropObjectsNotInSource = true });

        var drop = Assert.IsType<DropDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(MariaDbElementTypes.SqlForeignKeyConstraint, drop.Element.Type);
        Assert.False(drop.CausesDataLoss);

        Assert.Contains(
            "ALTER TABLE `Orders` DROP FOREIGN KEY `FK_Orders_Customers`;", Script(comparison));
    }

    /// <summary>
    /// Moving the PRIMARY KEY to another column is a redefinition under the engine-fixed name,
    /// so it must be a drop-and-recreate rather than a create or a drop.
    /// </summary>
    [Fact]
    public async Task MovePrimaryKey_ProducesRecreateDelta()
    {
        var comparison = await CompareAsync(
            "CREATE TABLE Table1 (Id int NOT NULL, AlternatePK int NOT NULL, PRIMARY KEY (Id));",
            """
            CREATE TABLE Table1 (Id int NOT NULL, AlternatePK int NOT NULL,
                PRIMARY KEY (AlternatePK));
            """);

        var recreate = Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(MariaDbElementTypes.SqlPrimaryKeyConstraint, recreate.SourceElement.Type);

        var sql = Script(comparison);

        // The old key must be dropped before the new one is added, or the engine rejects a
        // second primary key on the table.
        var drop = sql.IndexOf("DROP PRIMARY KEY", StringComparison.Ordinal);
        var add = sql.IndexOf("ADD PRIMARY KEY", StringComparison.Ordinal);

        Assert.True(drop >= 0, $"Expected the old primary key to be dropped in:\n{sql}");
        Assert.True(add > drop, $"Expected the new primary key added after the drop in:\n{sql}");
        Assert.Contains("`AlternatePK`", sql);
    }

    /// <summary>
    /// A primary key added to a table that already exists — the table had none before — has
    /// nothing to drop, so it is a plain create.
    /// </summary>
    [Fact]
    public async Task AddPrimaryKeyToExistingTable_ProducesCreateDelta()
    {
        var comparison = await CompareAsync(
            "CREATE TABLE Table1 (Id int NOT NULL, Other int NOT NULL);",
            "CREATE TABLE Table1 (Id int NOT NULL, Other int NOT NULL, PRIMARY KEY (Id));");

        var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(MariaDbElementTypes.SqlPrimaryKeyConstraint, create.Element.Type);

        Assert.Contains("ALTER TABLE `Table1` ADD PRIMARY KEY (`Id`);", Script(comparison));
    }

    /// <summary>
    /// A constraint on a column being added in the same change must be scripted AFTER the column
    /// exists (issue #200). The standalone CreateDelta for the constraint and the AlterDelta
    /// adding the column are separate deltas, and ordering the creates first put the constraint
    /// ahead of its own column, which the engine rejects — aborting the deploy half-applied.
    /// The defect is in the shared <see cref="SchemaCompare"/>, so it shows up here identically.
    /// </summary>
    [Fact]
    public async Task AddColumnAndCheckConstraintOnIt_ScriptsTheColumnFirst()
    {
        var comparison = await CompareAsync(
            "CREATE TABLE Orders (Id int NOT NULL PRIMARY KEY);",
            """
            CREATE TABLE Orders (Id int NOT NULL PRIMARY KEY, Quantity int NULL,
                CONSTRAINT CK_Orders_Quantity CHECK (Quantity > 0));
            """);

        var sql = Script(comparison);

        var addColumn = sql.IndexOf("ADD COLUMN", StringComparison.Ordinal);
        var addConstraint = sql.IndexOf("ADD CONSTRAINT", StringComparison.Ordinal);

        Assert.True(addColumn >= 0, $"Expected the new column to be added in:\n{sql}");
        Assert.True(
            addConstraint > addColumn,
            $"Expected the constraint added after the column it references in:\n{sql}");
    }

    /// <summary>
    /// The same ordering for an index on a newly added column: CREATE INDEX names the column,
    /// so it cannot precede the ALTER that adds it.
    /// </summary>
    [Fact]
    public async Task AddColumnAndIndexOnIt_ScriptsTheColumnFirst()
    {
        var comparison = await CompareAsync(
            "CREATE TABLE Orders (Id int NOT NULL PRIMARY KEY);",
            """
            CREATE TABLE Orders (Id int NOT NULL PRIMARY KEY, Email varchar(255) NULL);
            CREATE INDEX IX_Orders_Email ON Orders (Email);
            """);

        var sql = Script(comparison);

        var addColumn = sql.IndexOf("ADD COLUMN", StringComparison.Ordinal);
        var createIndex = sql.IndexOf("CREATE INDEX", StringComparison.Ordinal);

        Assert.True(addColumn >= 0, $"Expected the new column to be added in:\n{sql}");
        Assert.True(
            createIndex > addColumn,
            $"Expected the index created after the column it references in:\n{sql}");
    }

    /// <summary>
    /// The regression guard for the deferred-FK machinery: when the tables themselves are being
    /// created, their constraints ride along on the CREATE TABLE (or, for a cycle, on a deferred
    /// <see cref="AddConstraintDelta"/>). Making an FK reconcilable standalone must not also
    /// produce a second, duplicate CreateDelta for it.
    /// </summary>
    [Fact]
    public async Task ConstraintsOnNewTables_AreNotAlsoCreatedStandalone()
    {
        var model = await ParseAsync(TwoTablesWithForeignKey);
        var comparison = SchemaCompare.Compare(
            new MariaDbDatabaseProvider("Server=unused"), model, new Model());

        // Two tables, and nothing else: the FK and both PKs ride on their table's CREATE.
        Assert.Equal(2, comparison.Deltas.Count);
        Assert.All(comparison.Deltas, delta =>
            Assert.Equal(MariaDbElementTypes.SqlTable, Assert.IsType<CreateDelta>(delta).Element.Type));

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
            CREATE TABLE Husband (Id int NOT NULL PRIMARY KEY, WifeId int NULL,
                CONSTRAINT FK_Husband_Wife FOREIGN KEY (WifeId) REFERENCES Wife (Id));
            CREATE TABLE Wife (Id int NOT NULL PRIMARY KEY, HusbandId int NULL,
                CONSTRAINT FK_Wife_Husband FOREIGN KEY (HusbandId) REFERENCES Husband (Id));
            """);

        var comparison = SchemaCompare.Compare(
            new MariaDbDatabaseProvider("Server=unused"), model, new Model());

        var sql = Script(comparison);

        // Exactly one deferred ADD CONSTRAINT: the edge that closes the cycle, and no duplicate.
        Assert.Equal(1, sql.Split("ADD CONSTRAINT").Length - 1);
        Assert.Equal(2, sql.Split("CREATE TABLE").Length - 1);
    }
}
