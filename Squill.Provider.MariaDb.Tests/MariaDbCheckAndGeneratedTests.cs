using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Fast, Docker-free tests over the modeling and scripting of CHECK constraints and
/// generated (computed) columns (issue #120). Both were previously dropped silently, with
/// only a non-fatal SQ1002 warning. End-to-end behavior against real MariaDB and MySQL is
/// covered by the integration tests.
/// </summary>
public class MariaDbCheckAndGeneratedTests
{
    private static async Task<Model> BuildModelAsync(string sql)
    {
        var parser = new AntlrMariaDbParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(workspace, parser).ExtractModelAsync()).Model;
    }

    private static async Task<string> ScriptFromEmptyAsync(string sql)
    {
        var provider = new MariaDbDatabaseProvider("Server=unused");
        var comparison = SchemaCompare.Compare(provider, await BuildModelAsync(sql), new Model());

        return new MariaDbScriptGenerator().GenerateScript(comparison);
    }

    private static Element SingleColumn(Model model, string columnName)
        => model.Elements
            .Single(e => e.Type == MariaDbElementTypes.SqlTable)
            .Relationships.Single(r => r.Name == MariaDbRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == columnName);

    // ---- CHECK: modeling ----

    [Fact]
    public async Task NamedTableLevelCheck_BecomesCheckConstraintElement()
    {
        var model = await BuildModelAsync("""
            CREATE TABLE product
            (
                id INT PRIMARY KEY,
                price INT,
                CONSTRAINT ck_price CHECK (price > 0)
            );
            """);

        var check = Assert.Single(model.Elements,
            e => e.Type == MariaDbElementTypes.SqlCheckConstraint);

        Assert.Equal("ck_price", SqlName.UnqualifiedOf((string)check.Name!));
    }

    [Fact]
    public async Task NamedColumnLevelCheck_BecomesCheckConstraintElement()
    {
        var model = await BuildModelAsync("""
            CREATE TABLE product
            (
                id INT PRIMARY KEY,
                price INT CONSTRAINT ck_price CHECK (price > 0)
            );
            """);

        var check = Assert.Single(model.Elements,
            e => e.Type == MariaDbElementTypes.SqlCheckConstraint);

        Assert.Equal("ck_price", SqlName.UnqualifiedOf((string)check.Name!));
    }

    /// <summary>
    /// MariaDB names an unnamed table-level CHECK <c>CONSTRAINT_1</c> while MySQL names it
    /// <c>&lt;table&gt;_chk_1</c>. One DACPAC serves both engines, so the name cannot be
    /// predicted at build time and an unnamed CHECK is a build error rather than a
    /// constraint that re-diffs on every deploy.
    /// </summary>
    [Fact]
    public async Task UnnamedTableLevelCheck_IsABuildError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync("""
            CREATE TABLE product
            (
                id INT PRIMARY KEY,
                price INT,
                CHECK (price > 0)
            );
            """));

        Assert.Contains("has no name", ex.Message);
        Assert.Contains("CONSTRAINT <name> CHECK", ex.Message);
    }

    [Fact]
    public async Task UnnamedColumnLevelCheck_IsABuildError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync("""
            CREATE TABLE product
            (
                id INT PRIMARY KEY,
                price INT CHECK (price > 0)
            );
            """));

        Assert.Contains("has no name", ex.Message);
    }

    [Fact]
    public async Task DuplicateCheckConstraintName_IsABuildError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync("""
            CREATE TABLE product
            (
                id INT PRIMARY KEY,
                price INT,
                stock INT,
                CONSTRAINT ck_positive CHECK (price > 0),
                CONSTRAINT ck_positive CHECK (stock > 0)
            );
            """));

        Assert.Contains("already", ex.Message);
    }

    [Fact]
    public async Task CheckExpression_DoesNotParticipateInIdentity()
    {
        var model = await BuildModelAsync("""
            CREATE TABLE product
            (
                id INT PRIMARY KEY,
                price INT,
                CONSTRAINT ck_price CHECK (price > 0)
            );
            """);

        var check = Assert.Single(model.Elements,
            e => e.Type == MariaDbElementTypes.SqlCheckConstraint);

        Assert.False(Assert.Single(check.Properties,
            p => p.Name == MariaDbPropertyNames.CheckExpression).ParticipatesInIdentity);
    }

    // ---- CHECK: scripting ----

    [Fact]
    public async Task CheckConstraint_IsScriptedAsTableLevelClause()
    {
        var sql = await ScriptFromEmptyAsync("""
            CREATE TABLE product
            (
                id INT PRIMARY KEY,
                price INT,
                CONSTRAINT ck_price CHECK (price > 0)
            );
            """);

        Assert.Contains("CONSTRAINT `ck_price` CHECK (price > 0)", sql);
    }

    [Fact]
    public async Task CheckAddedToExistingTable_IsScriptedAsAlterTable()
    {
        const string target = """
            CREATE TABLE product
            (
                id INT PRIMARY KEY,
                price INT
            );
            """;

        const string source = """
            CREATE TABLE product
            (
                id INT PRIMARY KEY,
                price INT,
                CONSTRAINT ck_price CHECK (price > 0)
            );
            """;

        var provider = new MariaDbDatabaseProvider("Server=unused");

        var comparison = SchemaCompare.Compare(
            provider, await BuildModelAsync(source), await BuildModelAsync(target));

        var sql = new MariaDbScriptGenerator().GenerateScript(comparison);

        Assert.Contains("ALTER TABLE `product` ADD CONSTRAINT `ck_price` CHECK (price > 0);", sql);
    }

    // ---- Generated columns: modeling ----

    [Fact]
    public async Task StoredGeneratedColumn_RecordsExpressionAndStorage()
    {
        var model = await BuildModelAsync("""
            CREATE TABLE line_item
            (
                id INT PRIMARY KEY,
                price INT NOT NULL,
                quantity INT NOT NULL,
                total INT GENERATED ALWAYS AS (price * quantity) STORED
            );
            """);

        var total = SingleColumn(model, "total");

        Assert.True(total.GetProperty<bool?>(MariaDbPropertyNames.IsStored));
        Assert.Equal("price * quantity",
            total.GetProperty<string>(MariaDbPropertyNames.GeneratedExpression));
    }

    /// <summary>
    /// MariaDB defaults a generated column to VIRTUAL when no storage kind is written, and
    /// accepts PERSISTENT as a synonym for STORED.
    /// </summary>
    [Theory]
    [InlineData("VIRTUAL", false)]
    [InlineData("STORED", true)]
    [InlineData("PERSISTENT", true)]
    [InlineData("", false)]
    public async Task GeneratedColumn_StorageKindIsRecorded(string storage, bool expectedIsStored)
    {
        var model = await BuildModelAsync($"""
            CREATE TABLE line_item
            (
                id INT PRIMARY KEY,
                price INT NOT NULL,
                doubled INT GENERATED ALWAYS AS (price * 2) {storage}
            );
            """);

        Assert.Equal(expectedIsStored,
            SingleColumn(model, "doubled").GetProperty<bool?>(MariaDbPropertyNames.IsStored));
    }

    [Fact]
    public async Task GeneratedExpression_DoesNotParticipateInIdentity_ButIsStoredDoes()
    {
        var model = await BuildModelAsync("""
            CREATE TABLE line_item
            (
                id INT PRIMARY KEY,
                price INT NOT NULL,
                doubled INT GENERATED ALWAYS AS (price * 2) STORED
            );
            """);

        var doubled = SingleColumn(model, "doubled");

        Assert.False(Assert.Single(doubled.Properties,
            p => p.Name == MariaDbPropertyNames.GeneratedExpression).ParticipatesInIdentity);

        Assert.True(Assert.Single(doubled.Properties,
            p => p.Name == MariaDbPropertyNames.IsStored).ParticipatesInIdentity);
    }

    // ---- Generated columns: scripting ----

    [Fact]
    public async Task StoredGeneratedColumn_IsScriptedWithGenerationClause()
    {
        var sql = await ScriptFromEmptyAsync("""
            CREATE TABLE line_item
            (
                id INT PRIMARY KEY,
                price INT NOT NULL,
                quantity INT NOT NULL,
                total INT GENERATED ALWAYS AS (price * quantity) STORED
            );
            """);

        Assert.Contains("`total` int GENERATED ALWAYS AS (price * quantity) STORED", sql);
    }

    [Fact]
    public async Task VirtualGeneratedColumn_IsScriptedAsVirtual()
    {
        var sql = await ScriptFromEmptyAsync("""
            CREATE TABLE line_item
            (
                id INT PRIMARY KEY,
                price INT NOT NULL,
                doubled INT GENERATED ALWAYS AS (price * 2) VIRTUAL
            );
            """);

        Assert.Contains("`doubled` int GENERATED ALWAYS AS (price * 2) VIRTUAL", sql);
    }

    /// <summary>
    /// A generated column takes no explicit NULL suffix — the generation clause replaces
    /// the usual nullability/DEFAULT tail.
    /// </summary>
    [Fact]
    public async Task GeneratedColumn_IsNotScriptedWithNullSuffix()
    {
        var sql = await ScriptFromEmptyAsync("""
            CREATE TABLE line_item
            (
                id INT PRIMARY KEY,
                price INT NOT NULL,
                doubled INT GENERATED ALWAYS AS (price * 2) STORED
            );
            """);

        Assert.DoesNotContain("STORED NULL", sql);
    }

    /// <summary>
    /// MySQL accepts <c>GENERATED ALWAYS AS (...) STORED NOT NULL</c> but MariaDB rejects it
    /// inside a CREATE TABLE, in either position. One DACPAC serves both engines, so there
    /// is no portable spelling to generate — the build rejects it rather than emitting DDL
    /// that fails to deploy on one of the two engines.
    /// </summary>
    [Fact]
    public async Task NotNullGeneratedColumn_IsABuildError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync("""
            CREATE TABLE line_item
            (
                id INT PRIMARY KEY,
                price INT NOT NULL,
                doubled INT GENERATED ALWAYS AS (price * 2) STORED NOT NULL
            );
            """));

        Assert.Contains("NOT NULL", ex.Message);
        Assert.Contains("generated column", ex.Message);
    }
}
