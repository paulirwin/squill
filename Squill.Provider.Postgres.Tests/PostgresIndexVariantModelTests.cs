using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Docker-free tests over the CREATE INDEX facets of issue #160 — a per-key COLLATE, NULLS NOT
/// DISTINCT and INCLUDE covering columns — plus the table-level DEFERRABLE spelling: the model
/// shape each produces and how each is scripted back out.
///
/// These pin down the same omit-when-default convention #159 established. PostgreSQL resolves
/// each facet into a catalog value whether or not the source declared it — every collatable key
/// column reports a collation, every index reports indnullsnotdistinct — so the model records
/// one only when it differs from the default. Recording them unconditionally would stop a parsed
/// model hash-matching an extracted one, and every deploy would re-diff.
/// </summary>
public class PostgresIndexVariantModelTests
{
    private static readonly PostgresDatabaseProvider Provider = new("Host=unused");

    private const string PeopleTable = """
CREATE TABLE people
(
    id         integer PRIMARY KEY,
    name       text,
    age        integer,
    first_name text,
    last_name  text
);
""";

    private static Task<Model> ParseModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    private static async Task<Element> SingleIndexAsync(string indexSql)
    {
        var model = await ParseModelAsync($"{PeopleTable}\n{indexSql}");

        return Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlIndex);
    }

    private static async Task<string> ScriptAsync(string sql)
    {
        var source = await ParseModelAsync(sql);
        var comparison = SchemaCompare.Compare(Provider, source, new Model());

        return new PostgresScriptGenerator().GenerateScript(comparison);
    }

    private static Element SingleKeyColumn(Element index)
        => Assert.Single(
            index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications)!
                .Entries.OfType<Element>());

    [Fact]
    public async Task IndexElementCollate_IsStoredOnTheKeyColumn()
    {
        var index = await SingleIndexAsync(
            """CREATE INDEX ix_people_name ON people (name COLLATE "POSIX");""");

        Assert.Equal("POSIX",
            SingleKeyColumn(index).GetProperty<string>(PostgresPropertyNames.Collation));
    }

    /// <summary>
    /// The load-bearing half of the convention: an index that declares no collation must carry
    /// no collation property, or it could never hash-match the extracted model — where the
    /// catalog reports a resolved "default" collation for the same column.
    /// </summary>
    [Fact]
    public async Task IndexWithoutCollate_StoresNoCollation()
    {
        var index = await SingleIndexAsync("CREATE INDEX ix_people_name ON people (name);");

        Assert.Null(SingleKeyColumn(index).GetProperty<string>(PostgresPropertyNames.Collation));
    }

    /// <summary>
    /// The catalog reports an opclass and a collation unqualified, so a schema-qualified source
    /// spelling must reduce to the same bare name — otherwise a qualified declaration re-diffs
    /// against the very database it just deployed.
    /// </summary>
    [Fact]
    public async Task SchemaQualifiedCollationAndOperatorClass_AreStoredUnqualified()
    {
        var index = await SingleIndexAsync(
            """CREATE INDEX ix ON people (name COLLATE pg_catalog."POSIX" pg_catalog.text_pattern_ops);""");

        var keyColumn = SingleKeyColumn(index);

        Assert.Equal("POSIX", keyColumn.GetProperty<string>(PostgresPropertyNames.Collation));
        Assert.Equal("text_pattern_ops",
            keyColumn.GetProperty<string>(PostgresPropertyNames.OperatorClass));
    }

    [Fact]
    public async Task NullsNotDistinct_IsStoredOnTheIndex()
    {
        var index = await SingleIndexAsync(
            "CREATE UNIQUE INDEX ix_people_age ON people (age) NULLS NOT DISTINCT;");

        Assert.True(index.GetProperty<bool?>(PostgresPropertyNames.NullsNotDistinct));
    }

    [Fact]
    public async Task IndexWithoutNullsNotDistinct_StoresNoProperty()
    {
        var index = await SingleIndexAsync("CREATE UNIQUE INDEX ix_people_age ON people (age);");

        Assert.Null(index.GetProperty<bool?>(PostgresPropertyNames.NullsNotDistinct));
    }

    /// <summary>
    /// Covering columns are held in their own relationship rather than among the key columns,
    /// because they take no part in the index key — so the key column list must be unchanged by
    /// their presence.
    /// </summary>
    [Fact]
    public async Task IncludeColumns_AreStoredApartFromTheKeyColumns()
    {
        var index = await SingleIndexAsync(
            "CREATE INDEX ix ON people (name) INCLUDE (first_name, last_name);");

        Assert.Equal("people.name",
            SingleKeyColumn(index).GetRelationship(PostgresRelationshipNames.Column)!
                .Entries.OfType<Reference>().Single().Name);

        var included = index.GetRelationship(PostgresRelationshipNames.IncludedColumns);

        Assert.NotNull(included);
        Assert.Collection(included.Entries.OfType<Reference>(),
            r => Assert.Equal("people.first_name", r.Name),
            r => Assert.Equal("people.last_name", r.Name));
    }

    [Fact]
    public async Task IndexWithoutInclude_HasNoIncludedColumnsRelationship()
    {
        var index = await SingleIndexAsync("CREATE INDEX ix ON people (name);");

        Assert.Null(index.GetRelationship(PostgresRelationshipNames.IncludedColumns));
    }

    [Fact]
    public async Task IndexElementCollate_IsScriptedBeforeTheOperatorClass()
    {
        var script = await ScriptAsync(PeopleTable
            + """

CREATE INDEX ix ON people (name COLLATE "POSIX" text_pattern_ops DESC);
""");

        // The order PostgreSQL's CREATE INDEX synopsis requires. The trailing NULLS LAST is
        // the pre-existing default-suppression rule at work: DESC implies NULLS FIRST, so the
        // btree default this model carries has to be spelled out.
        Assert.Contains("""("name" COLLATE "POSIX" text_pattern_ops DESC NULLS LAST)""", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullsNotDistinct_IsScripted()
    {
        var script = await ScriptAsync(PeopleTable
            + "\nCREATE UNIQUE INDEX ix_people_age ON people (age) NULLS NOT DISTINCT;");

        Assert.Contains("NULLS NOT DISTINCT", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncludeColumns_AreScriptedAfterTheKeyList()
    {
        var script = await ScriptAsync(PeopleTable
            + "\nCREATE INDEX ix ON people (name) INCLUDE (first_name, last_name);");

        Assert.Contains("""("name") INCLUDE ("first_name", "last_name")""", script,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// With every facet at once, the clause order is the one the grammar demands:
    /// (keys) INCLUDE (...) NULLS NOT DISTINCT WITH (...) WHERE ...
    /// </summary>
    [Fact]
    public async Task AllIndexFacetsTogether_AreScriptedInGrammarOrder()
    {
        var script = await ScriptAsync(PeopleTable
            + """

CREATE UNIQUE INDEX ix ON people (name COLLATE "POSIX") INCLUDE (last_name)
    NULLS NOT DISTINCT WITH (fillfactor=70) WHERE (age > 18);
""");

        var expected =
            """CREATE UNIQUE INDEX "ix" ON "people" ("name" COLLATE "POSIX") """
            + """INCLUDE ("last_name") NULLS NOT DISTINCT WITH (fillfactor=70) WHERE ("age" > 18);""";

        Assert.Contains(expected, script, StringComparison.Ordinal);
    }

    /// <summary>
    /// pg_default is the only tablespace Squill accepts, and it is dropped rather than modeled:
    /// measured, an index placed there stores reltablespace = 0 exactly as one with no clause
    /// does, and pg_get_indexdef omits it — so the clause is a genuine no-op.
    /// </summary>
    [Fact]
    public async Task DefaultTablespace_IsAcceptedAndNotModeled()
    {
        var withClause = await ParseModelAsync(
            $"{PeopleTable}\nCREATE INDEX ix ON people (name) TABLESPACE pg_default;");
        var withoutClause = await ParseModelAsync(
            $"{PeopleTable}\nCREATE INDEX ix ON people (name);");

        // Indistinguishable in the catalog, so indistinguishable in the model.
        Assert.True(HashUtility.HashesEqual(withClause.Hash, withoutClause.Hash));
    }

    /// <summary>
    /// A non-default tablespace is a real placement decision. Squill cannot model it, so it is
    /// rejected rather than silently dropped — the principle #160 is built on.
    /// </summary>
    [Fact]
    public async Task NonDefaultTablespace_IsRejectedRatherThanDropped()
    {
        // Surfaced as a source-anchored build error rather than a raw stack trace — the
        // secondary defect #159 fixed, which this inherits by throwing from within the builder.
        var exception = await Assert.ThrowsAsync<SqlSourceException>(() => ParseModelAsync(
            $"{PeopleTable}\nCREATE INDEX ix ON people (name) TABLESPACE fast_ssd;"));

        var inner = Assert.IsType<NotSupportedException>(exception.InnerException);

        Assert.Contains("fast_ssd", inner.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A quoted identifier is case-sensitive, so <c>"PG_DEFAULT"</c> is not the default
    /// tablespace. Measured on PostgreSQL 18.4, an index declared
    /// <c>TABLESPACE "PG_DEFAULT"</c> fails with <c>tablespace "PG_DEFAULT" does not exist</c>,
    /// so accepting it would let the build pass a statement the deploy cannot run.
    /// </summary>
    [Fact]
    public async Task QuotedNonDefaultCaseTablespace_IsRejected()
    {
        var exception = await Assert.ThrowsAsync<SqlSourceException>(() => ParseModelAsync(
            $"{PeopleTable}\nCREATE INDEX ix ON people (name) TABLESPACE \"PG_DEFAULT\";"));

        var inner = Assert.IsType<NotSupportedException>(exception.InnerException);

        Assert.Contains("PG_DEFAULT", inner.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Quoting the default in its own case still names the default, so it stays a no-op.
    /// </summary>
    [Fact]
    public async Task QuotedDefaultTablespace_IsAcceptedAndNotModeled()
    {
        var withClause = await ParseModelAsync(
            $"{PeopleTable}\nCREATE INDEX ix ON people (name) TABLESPACE \"pg_default\";");
        var withoutClause = await ParseModelAsync(
            $"{PeopleTable}\nCREATE INDEX ix ON people (name);");

        Assert.True(HashUtility.HashesEqual(withClause.Hash, withoutClause.Hash));
    }

    [Fact]
    public async Task TableLevelDeferrableForeignKey_IsStoredAndScripted()
    {
        const string sql = """
CREATE TABLE customers
(
    id integer PRIMARY KEY
);

CREATE TABLE orders
(
    id  integer PRIMARY KEY,
    cid integer,
    CONSTRAINT fk_orders FOREIGN KEY (cid) REFERENCES customers (id) DEFERRABLE INITIALLY DEFERRED
);
""";

        var model = await ParseModelAsync(sql);
        var fk = Assert.Single(model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);

        Assert.True(fk.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable));
        Assert.True(fk.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred));

        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", await ScriptAsync(sql),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The inline and table-level spellings declare the same thing, so they must reduce to the
    /// same model — the asymmetry #160 opens with was that one refused to build while the other
    /// silently lied.
    /// </summary>
    [Fact]
    public async Task InlineAndTableLevelDeferrable_ProduceTheSameModel()
    {
        var inline = await ParseModelAsync("""
CREATE TABLE customers (id integer PRIMARY KEY);
CREATE TABLE orders
(
    id  integer PRIMARY KEY,
    cid integer CONSTRAINT fk_orders REFERENCES customers (id) DEFERRABLE INITIALLY DEFERRED
);
""");

        var tableLevel = await ParseModelAsync("""
CREATE TABLE customers (id integer PRIMARY KEY);
CREATE TABLE orders
(
    id  integer PRIMARY KEY,
    cid integer,
    CONSTRAINT fk_orders FOREIGN KEY (cid) REFERENCES customers (id) DEFERRABLE INITIALLY DEFERRED
);
""");

        Assert.True(HashUtility.HashesEqual(inline.Hash, tableLevel.Hash));
    }

    [Fact]
    public async Task TableLevelForeignKeyWithoutAttributes_StoresNoDeferrability()
    {
        var model = await ParseModelAsync("""
CREATE TABLE customers (id integer PRIMARY KEY);
CREATE TABLE orders
(
    cid integer,
    CONSTRAINT fk_orders FOREIGN KEY (cid) REFERENCES customers (id)
);
""");

        var fk = Assert.Single(model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);

        Assert.Null(fk.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable));
        Assert.Null(fk.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred));
    }
}
