using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over the modeling and scripting of UNIQUE constraints, the
/// serial types, and adding an index to a table that already exists (issue #121).
/// End-to-end behavior against real Postgres is covered by the integration tests.
/// </summary>
public class PostgresUniqueAndSerialTests
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

    private static async Task<SchemaComparison> CompareAsync(string sourceSql, string targetSql)
    {
        var provider = new PostgresDatabaseProvider("Host=unused");

        var options = new DeployOptions { BlockOnPossibleDataLoss = false };

        return SchemaCompare.Compare(
            provider, await BuildModelAsync(sourceSql), await BuildModelAsync(targetSql), options);
    }

    // ---- UNIQUE: modeling ----

    [Fact]
    public async Task ColumnLevelUnique_BecomesUniqueConstraintElement()
    {
        var model = await BuildModelAsync("""
CREATE TABLE users
(
    id integer PRIMARY KEY,
    email varchar(255) UNIQUE
);
""");

        var unique = Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlUniqueConstraint);

        // Postgres names an unnamed unique constraint <table>_<col>_key.
        Assert.Equal("users_email_key", SqlName.UnqualifiedOf((string)unique.Name!));
    }

    [Fact]
    public async Task TableLevelUnique_UsesPostgresDerivedNameForAllColumns()
    {
        var model = await BuildModelAsync("""
CREATE TABLE users
(
    id integer PRIMARY KEY,
    tenant_id integer NOT NULL,
    email varchar(255) NOT NULL,
    UNIQUE (tenant_id, email)
);
""");

        var unique = Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlUniqueConstraint);

        Assert.Equal("users_tenant_id_email_key", SqlName.UnqualifiedOf((string)unique.Name!));
    }

    [Fact]
    public async Task NamedUnique_KeepsItsName()
    {
        var model = await BuildModelAsync("""
CREATE TABLE users
(
    id integer PRIMARY KEY,
    email varchar(255),
    CONSTRAINT uq_users_email UNIQUE (email)
);
""");

        var unique = Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlUniqueConstraint);

        Assert.Equal("uq_users_email", SqlName.UnqualifiedOf((string)unique.Name!));
    }

    // ---- UNIQUE: scripting ----

    [Fact]
    public async Task CreateTable_EmitsUniqueConstraintClause()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE users
(
    id integer PRIMARY KEY,
    email varchar(255) NOT NULL,
    CONSTRAINT uq_users_email UNIQUE (email)
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("CONSTRAINT \"uq_users_email\" UNIQUE (\"email\")", sql);

        // A unique constraint is a constraint, not a standalone index.
        Assert.DoesNotContain("CREATE UNIQUE INDEX", sql);
    }

    [Fact]
    public async Task CreateTable_EmitsCompositeUniqueConstraintInDeclaredColumnOrder()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE users
(
    tenant_id integer NOT NULL,
    email varchar(255) NOT NULL,
    UNIQUE (tenant_id, email)
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains(
            "CONSTRAINT \"users_tenant_id_email_key\" UNIQUE (\"tenant_id\", \"email\")", sql);
    }

    /// <summary>
    /// Postgres requires a foreign key to be backed by an exact unique column set. A UNIQUE
    /// constraint provides one, so referencing it must build rather than be rejected as an
    /// unbacked reference.
    /// </summary>
    [Fact]
    public async Task ForeignKey_MayReferenceAUniqueColumn()
    {
        var model = await BuildModelAsync("""
CREATE TABLE users
(
    id integer PRIMARY KEY,
    email varchar(255) NOT NULL UNIQUE
);

CREATE TABLE logins
(
    id integer PRIMARY KEY,
    email varchar(255) NOT NULL REFERENCES users (email)
);
""");

        Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    /// <summary>
    /// Two unnamed unique constraints can derive the same <c>&lt;table&gt;_&lt;cols&gt;_key</c>
    /// name — <c>UNIQUE (a_b)</c> and <c>UNIQUE (a, b)</c> both give <c>t_a_b_key</c>.
    /// Postgres resolves that by appending a uniquifying suffix the model cannot predict, so
    /// it is reported as a duplicate rather than deployed under a name that would not match.
    /// </summary>
    [Fact]
    public async Task CollidingDerivedUniqueNames_AreABuildError()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, """
CREATE TABLE t
(
    a_b integer NOT NULL,
    a   integer NOT NULL,
    b   integer NOT NULL,
    UNIQUE (a_b),
    UNIQUE (a, b)
);
"""));

        var builder = new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SqlSourceException.DuplicateDefinition, ex.Code);
        Assert.Contains("t_a_b_key", ex.Message);
    }

    /// <summary>
    /// Postgres truncates a generated constraint name to 63 bytes, shortening the table and
    /// column parts from the middle while keeping the <c>_key</c> suffix. Squill does not
    /// reproduce that, so rather than predict a name the database will never use (which would
    /// re-diff on every deploy), it asks for an explicit one.
    /// </summary>
    [Fact]
    public async Task OverlongDerivedUniqueName_AsksForAnExplicitName()
    {
        var longColumn = new string('a', 70);

        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, $"""
CREATE TABLE t
(
    {longColumn} integer NOT NULL UNIQUE
);
"""));

        var builder = new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SqlSourceException.InvalidConstraint, ex.Code);
        Assert.Contains("63-byte identifier limit", ex.Message);
    }

    /// <summary>
    /// An explicitly named constraint is used verbatim, so the 63-byte guard does not apply
    /// to a table/column pair that would only have been a problem when derived.
    /// </summary>
    [Fact]
    public async Task OverlongColumns_WithExplicitUniqueName_Build()
    {
        var longColumn = new string('a', 70);

        var model = await BuildModelAsync($"""
CREATE TABLE t
(
    {longColumn} integer NOT NULL,
    CONSTRAINT uq_t UNIQUE ({longColumn})
);
""");

        var unique = Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlUniqueConstraint);

        Assert.Equal("uq_t", SqlName.UnqualifiedOf((string)unique.Name!));
    }

    // ---- UNIQUE: incremental deploy ----

    [Fact]
    public async Task AddedUnique_OnExistingTable_EmitsAlterTableAddConstraint()
    {
        const string before = """
CREATE TABLE users
(
    id integer PRIMARY KEY,
    email varchar(255) NOT NULL
);
""";

        const string after = """
CREATE TABLE users
(
    id integer PRIMARY KEY,
    email varchar(255) NOT NULL,
    CONSTRAINT uq_users_email UNIQUE (email)
);
""";

        var comparison = await CompareAsync(after, before);

        var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(PostgresElementTypes.SqlUniqueConstraint, create.Element.Type);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains(
            "ALTER TABLE \"users\" ADD CONSTRAINT \"uq_users_email\" UNIQUE (\"email\");", sql);
    }

    // ---- Standalone CREATE INDEX on an existing table ----

    /// <summary>
    /// Adding an index to a table that already exists has no CREATE TABLE to hang off, so it
    /// must produce a CreateDelta of its own. Previously this produced no delta at all and
    /// the index was silently never created.
    /// </summary>
    [Fact]
    public async Task AddedIndex_OnExistingTable_EmitsCreateIndex()
    {
        const string before = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title varchar(255) NOT NULL
);
""";

        const string after = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title varchar(255) NOT NULL
);

CREATE INDEX idx_film_title ON film (title);
""";

        var comparison = await CompareAsync(after, before);

        var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(PostgresElementTypes.SqlIndex, create.Element.Type);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("CREATE INDEX \"idx_film_title\" ON \"film\"", sql);
        Assert.Contains("(\"title\")", sql);
    }

    [Fact]
    public async Task UnchangedIndex_OnUnchangedTable_EmitsNoDelta()
    {
        const string schema = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title varchar(255) NOT NULL
);

CREATE INDEX idx_film_title ON film (title);
""";

        var comparison = await CompareAsync(schema, schema);

        Assert.Empty(comparison.Deltas);
    }

    // ---- SERIAL ----

    [Theory]
    [InlineData("smallserial", "smallint")]
    [InlineData("serial", "integer")]
    [InlineData("bigserial", "bigint")]
    public async Task SerialColumn_IsLoweredToIdentityOverUnderlyingIntegerType(
        string serialType, string expectedType)
    {
        var comparison = await CompareToEmptyAsync($"""
CREATE TABLE widgets
(
    id {serialType} PRIMARY KEY,
    name text NOT NULL
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // The column deploys as the real underlying integer type, not the literal
        // shorthand, backed by an identity rather than a bare nextval default.
        Assert.Contains($"\"id\" {expectedType} GENERATED BY DEFAULT AS IDENTITY", sql);
        Assert.DoesNotContain(serialType, sql);
    }

    /// <summary>
    /// A serial column is implicitly NOT NULL, and an identity column is scripted without a
    /// nullability suffix (Postgres rejects an explicit NULL on one).
    /// </summary>
    [Fact]
    public async Task SerialColumn_IsNotNullable()
    {
        var model = await BuildModelAsync("""
CREATE TABLE widgets
(
    id serial PRIMARY KEY
);
""");

        var table = Assert.Single(model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var column = table.Relationships
            .Single(r => r.Name == PostgresRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single();

        Assert.False(column.GetProperty<bool?>(PostgresPropertyNames.IsNullable));
        Assert.True(column.GetProperty<bool?>(PostgresPropertyNames.IsIdentity));
    }
}
