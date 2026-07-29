using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Docker-free tests over column-level COLLATE, inline DEFERRABLE foreign keys, and
/// CREATE COLLATION (issue #159): the model shape each produces and how each is scripted.
///
/// The omit-when-default convention is what most of these pin down. PostgreSQL resolves every
/// one of these facets into a catalog value even when the source declared nothing — a collatable
/// column always reports a collation, a constraint always reports its deferrability — so the
/// model stores each only when it is not the default. Storing them unconditionally would leave
/// a parsed model unable to hash-match an extracted one, and every deploy would re-diff.
/// </summary>
public class PostgresCollationAndDeferrableTests
{
    private static readonly PostgresDatabaseProvider Provider = new("Host=unused");

    private static Task<Model> ParseModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    private static async Task<Element> SingleElementAsync(string sql, string elementType)
    {
        var model = await ParseModelAsync(sql);

        return Assert.Single(model.Elements, i => i.Type == elementType);
    }

    private static async Task<string> ScriptAsync(string sql)
    {
        var source = await ParseModelAsync(sql);
        var comparison = SchemaCompare.Compare(Provider, source, new Model());

        return new PostgresScriptGenerator().GenerateScript(comparison);
    }

    private static async Task<Element> ColumnAsync(string sql, string columnName)
    {
        var table = await SingleElementAsync(sql, PostgresElementTypes.SqlTable);

        return Assert.Single(
            RelationshipHelpers.GetOrderedColumns(table),
            c => SqlName.UnqualifiedOf(c.Name) == columnName).Column;
    }

    // ---- Column COLLATE ----

    [Fact]
    public async Task ColumnCollate_IsStoredOnTheColumn()
    {
        var column = await ColumnAsync(
            """CREATE TABLE people (ssn character varying(11) COLLATE "POSIX");""", "ssn");

        Assert.Equal("POSIX", column.GetProperty<string>(PostgresPropertyNames.Collation));
    }

    [Fact]
    public async Task ColumnWithoutCollate_StoresNoCollation()
    {
        var column = await ColumnAsync("CREATE TABLE people (name text);", "name");

        Assert.Null(column.GetProperty<string>(PostgresPropertyNames.Collation));
    }

    /// <summary>
    /// An explicit COLLATE "default" names the collation every collatable column already has.
    /// pg_attribute reports it identically to a column with no COLLATE at all, so storing it
    /// would leave the column re-diffing forever.
    /// </summary>
    [Fact]
    public async Task ExplicitDefaultCollation_StoresNoCollation()
    {
        var column = await ColumnAsync(
            """CREATE TABLE people (name text COLLATE "default");""", "name");

        Assert.Null(column.GetProperty<string>(PostgresPropertyNames.Collation));
    }

    [Fact]
    public async Task ColumnCollate_IsScriptedAfterTheType()
    {
        var sql = await ScriptAsync(
            """CREATE TABLE people (ssn character varying(11) COLLATE "POSIX" NOT NULL);""");

        Assert.Contains("""ssn" varchar(11) COLLATE "POSIX" NOT NULL""", sql,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A collation change has no SET form — it rides on ALTER COLUMN ... TYPE — and dropping one
    /// needs an explicit COLLATE "default", since omitting the clause keeps the existing
    /// collation rather than resetting it.
    /// </summary>
    [Fact]
    public async Task AddingACollation_IsScriptedAsAnAlterColumnType()
    {
        var source = await ParseModelAsync(
            """CREATE TABLE people (name text COLLATE "POSIX");""");
        var target = await ParseModelAsync("CREATE TABLE people (name text);");

        var sql = new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(Provider, source, target));

        Assert.Contains("""ALTER COLUMN "name" TYPE text COLLATE "POSIX";""", sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingACollation_ScriptsAnExplicitDefault()
    {
        var source = await ParseModelAsync("CREATE TABLE people (name text);");
        var target = await ParseModelAsync(
            """CREATE TABLE people (name text COLLATE "POSIX");""");

        var sql = new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(Provider, source, target));

        Assert.Contains("""ALTER COLUMN "name" TYPE text COLLATE "default";""", sql,
            StringComparison.Ordinal);
    }

    // ---- Inline DEFERRABLE foreign keys ----

    private static async Task<Element> ForeignKeyAsync(string sql)
    {
        var model = await ParseModelAsync(sql);

        return Assert.Single(
            model.Elements, i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    private const string Customers = "CREATE TABLE customers (id integer PRIMARY KEY);";

    [Fact]
    public async Task InlineDeferrableForeignKey_StoresBothFlags()
    {
        var fk = await ForeignKeyAsync(Customers + """
CREATE TABLE orders (cid integer REFERENCES customers (id) DEFERRABLE INITIALLY DEFERRED);
""");

        Assert.True(fk.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable));
        Assert.True(fk.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred));
    }

    [Fact]
    public async Task OrdinaryForeignKey_StoresNeitherFlag()
    {
        var fk = await ForeignKeyAsync(Customers + """
CREATE TABLE orders (cid integer REFERENCES customers (id));
""");

        Assert.Null(fk.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable));
        Assert.Null(fk.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred));
    }

    /// <summary>
    /// NOT DEFERRABLE INITIALLY IMMEDIATE is what PostgreSQL applies anyway, so spelling it out
    /// must produce the same model as writing nothing — otherwise the two spellings would not
    /// hash-match.
    /// </summary>
    [Fact]
    public async Task ExplicitlyNotDeferrable_StoresNeitherFlag()
    {
        var fk = await ForeignKeyAsync(Customers + """
CREATE TABLE orders (cid integer REFERENCES customers (id) NOT DEFERRABLE INITIALLY IMMEDIATE);
""");

        Assert.Null(fk.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable));
        Assert.Null(fk.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred));
    }

    /// <summary>
    /// INITIALLY DEFERRED implies DEFERRABLE — PostgreSQL rejects the pairing with NOT
    /// DEFERRABLE — so a source writing only the INITIALLY clause is deferrable too, which is
    /// how the catalog reports it.
    /// </summary>
    [Fact]
    public async Task InitiallyDeferredAlone_ImpliesDeferrable()
    {
        var fk = await ForeignKeyAsync(Customers + """
CREATE TABLE orders (cid integer REFERENCES customers (id) INITIALLY DEFERRED);
""");

        Assert.True(fk.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable));
        Assert.True(fk.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred));
    }

    [Fact]
    public async Task DeferrableForeignKey_IsScripted()
    {
        var sql = await ScriptAsync(Customers + """
CREATE TABLE orders (cid integer REFERENCES customers (id) DEFERRABLE INITIALLY DEFERRED);
""");

        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeferrableAlone_ScriptsWithoutAnInitiallyClause()
    {
        var sql = await ScriptAsync(Customers + """
CREATE TABLE orders (cid integer REFERENCES customers (id) DEFERRABLE);
""");

        Assert.Contains("DEFERRABLE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INITIALLY", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// A constraint attribute is only legal on a constraint that can be deferred. On a column
    /// with none, PostgreSQL rejects it — so Squill does too, rather than dropping something the
    /// source declared.
    /// </summary>
    [Fact]
    public async Task ConstraintAttributeWithoutAConstraint_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => ParseModelAsync("CREATE TABLE orders (cid integer DEFERRABLE);"));

        Assert.IsType<NotSupportedException>(ex.InnerException);
    }

    // ---- CREATE COLLATION ----

    [Fact]
    public async Task CreateCollation_WithLocale_FansOutToTheLcFacets()
    {
        var collation = await SingleElementAsync(
            "CREATE COLLATION some_collation (LOCALE = 'POSIX', PROVIDER = libc);",
            PostgresElementTypes.SqlCollation);

        Assert.Equal("libc", collation.GetProperty<string>(PostgresPropertyNames.Provider));

        // For libc, LOCALE sets both LC_COLLATE and LC_CTYPE — which is how pg_collation
        // stores it, with colllocale left empty.
        Assert.Equal("POSIX", collation.GetProperty<string>(PostgresPropertyNames.LcCollate));
        Assert.Equal("POSIX", collation.GetProperty<string>(PostgresPropertyNames.LcCtype));
        Assert.Null(collation.GetProperty<string>(PostgresPropertyNames.Locale));
    }

    [Fact]
    public async Task CreateCollation_WithIcuProvider_KeepsTheLocale()
    {
        var collation = await SingleElementAsync(
            "CREATE COLLATION nd (PROVIDER = icu, LOCALE = 'und', DETERMINISTIC = false);",
            PostgresElementTypes.SqlCollation);

        Assert.Equal("icu", collation.GetProperty<string>(PostgresPropertyNames.Provider));
        Assert.Equal("und", collation.GetProperty<string>(PostgresPropertyNames.Locale));
        Assert.Null(collation.GetProperty<string>(PostgresPropertyNames.LcCollate));
        Assert.False(collation.GetProperty<bool?>(PostgresPropertyNames.IsDeterministic));
    }

    [Fact]
    public async Task CreateCollation_DeterministicByDefault_StoresNoProperty()
    {
        var collation = await SingleElementAsync(
            "CREATE COLLATION c (LC_COLLATE = 'C', LC_CTYPE = 'C');",
            PostgresElementTypes.SqlCollation);

        Assert.Null(collation.GetProperty<bool?>(PostgresPropertyNames.IsDeterministic));

        // libc is what PostgreSQL assumes when no provider is declared.
        Assert.Equal("libc", collation.GetProperty<string>(PostgresPropertyNames.Provider));
    }

    [Fact]
    public async Task CreateCollation_IsScripted()
    {
        var sql = await ScriptAsync(
            "CREATE COLLATION some_collation (LOCALE = 'POSIX', PROVIDER = libc);");

        Assert.Contains(
            """CREATE COLLATION "some_collation" (PROVIDER = libc, LC_COLLATE = 'POSIX', """
            + "LC_CTYPE = 'POSIX');",
            sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// PostgreSQL stores a collation copied FROM another identically to one spelling out that
    /// collation's locale, keeping no record of the reference — so resolving it would need the
    /// copied collation's locale from a live server, which is exactly what this builder avoids.
    /// It is rejected rather than modeled into something that cannot round-trip.
    /// </summary>
    [Fact]
    public async Task CreateCollation_FromAnother_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => ParseModelAsync("""CREATE COLLATION mine FROM "POSIX";"""));

        Assert.IsType<NotSupportedException>(ex.InnerException);
    }

    /// <summary>
    /// A collation must be created before any table whose column declares COLLATE against it.
    /// </summary>
    [Fact]
    public async Task CreateCollation_IsScriptedBeforeTheTableUsingIt()
    {
        var sql = await ScriptAsync("""
CREATE TABLE people (ssn text COLLATE some_collation);
CREATE COLLATION some_collation (LOCALE = 'POSIX', PROVIDER = libc);
""");

        Assert.True(
            sql.IndexOf("CREATE COLLATION", StringComparison.Ordinal)
            < sql.IndexOf("CREATE TABLE", StringComparison.Ordinal),
            "The collation must be created before the table whose column collates against it.");
    }
}
