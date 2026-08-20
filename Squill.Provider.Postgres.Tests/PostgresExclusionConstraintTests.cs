using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Modeling and scripting of EXCLUDE constraints (issue #212).
///
/// An exclusion constraint generalises UNIQUE: instead of forbidding two rows whose keys are
/// equal, it forbids any pair for which every element's operator returns true, which is the
/// only declarative way PostgreSQL can express a non-overlap rule. Before this the whole
/// construct threw <c>NotImplementedException</c>, so a table declaring one could not be built.
///
/// The round-trip decisions asserted here were measured against a live server first, per
/// CLAUDE.md, and several are not what copying the index path would have produced.
/// </summary>
public class PostgresExclusionConstraintTests
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()));

    private static async Task<string> ScriptFromEmptyAsync(string sql)
    {
        var provider = new PostgresDatabaseProvider("Host=unused");
        var comparison = SchemaCompare.Compare(provider, await BuildModelAsync(sql), new Model());

        return new PostgresScriptGenerator().GenerateScript(comparison);
    }

    private static Element ExclusionOf(Model model)
        => Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlExclusionConstraint);

    private static IReadOnlyList<Element> ElementsOf(Element exclusion)
        => (exclusion.GetRelationship(PostgresRelationshipNames.ExclusionElements)?.Entries ?? [])
            .OfType<Element>()
            .Where(e => e.Type == PostgresElementTypes.SqlExclusionConstraintElement)
            .ToList();

    private static string OperatorOf(Element element)
        => element.GetProperty<string>(PostgresPropertyNames.ExclusionOperator)!;

    private static Element KeyOf(Element element)
        => Assert.Single(
            (element.GetRelationship(PostgresRelationshipNames.ColumnSpecifications)?.Entries ?? [])
                .OfType<Element>(),
            e => e.Type == PostgresElementTypes.SqlIndexedColumnSpecification);

    private static string KeyColumnOf(Element element)
        => Assert.Single(
                (KeyOf(element).GetRelationship(PostgresRelationshipNames.Column)?.Entries ?? [])
                    .OfType<Reference>())
            .Name.Split('.')[^1];

    private const string Booking =
        """
        CREATE TABLE booking (
            room integer,
            during tstzrange,
            CONSTRAINT no_overlap EXCLUDE USING gist (room WITH =, during WITH &&)
        );
        """;

    [Fact]
    public async Task ExclusionConstraint_ReachesTheModel()
    {
        var model = await BuildModelAsync(Booking);

        var exclusion = ExclusionOf(model);

        Assert.Equal("no_overlap", exclusion.Name);
        Assert.Equal("gist", exclusion.GetProperty<string>(PostgresPropertyNames.IndexMethod));

        var elements = ElementsOf(exclusion);

        Assert.Equal(2, elements.Count);
        Assert.Equal("room", KeyColumnOf(elements[0]));
        Assert.Equal("=", OperatorOf(elements[0]));
        Assert.Equal("during", KeyColumnOf(elements[1]));
        Assert.Equal("&&", OperatorOf(elements[1]));
    }

    // Measured: PostgreSQL reports an access method back for every exclusion constraint, so an
    // omitted USING comes back as `btree`. Resolving the default at build time rather than
    // storing the absence is what keeps a bare EXCLUDE from re-diffing against its own database.
    [Fact]
    public async Task ExclusionConstraint_WithoutUsing_DefaultsToBtree()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, b integer, EXCLUDE (a WITH =, b WITH =));");

        Assert.Equal("btree",
            ExclusionOf(model).GetProperty<string>(PostgresPropertyNames.IndexMethod));
    }

    // Postgres names an unnamed EXCLUDE <table>_<keys>_excl. Predicting it lets a parsed model
    // hash-match one extracted from the database.
    [Theory]
    [InlineData("CREATE TABLE t (a integer, b integer, EXCLUDE (a WITH =, b WITH =));",
        "t_a_b_excl")]
    [InlineData("CREATE TABLE t (a integer, EXCLUDE (a WITH =));", "t_a_excl")]
    // INCLUDE columns take part in the derived name, exactly as they do for a unique
    // constraint (issue #210): measured, this is t_a_c_excl and not t_a_excl.
    [InlineData("CREATE TABLE t (a integer, c integer, EXCLUDE (a WITH =) INCLUDE (c));",
        "t_a_c_excl")]
    // An expression key contributes the name of its outermost function...
    [InlineData("CREATE TABLE t (a text, EXCLUDE (lower(a) WITH =));", "t_lower_excl")]
    // ...its final segment when that call is schema-qualified...
    [InlineData("CREATE TABLE t (a text, EXCLUDE (pg_catalog.lower(a) WITH =));",
        "t_lower_excl")]
    // ...and the literal "expr" when the key is not a function call at all.
    [InlineData("CREATE TABLE t (a text, EXCLUDE ((a || 'x') WITH =));", "t_expr_excl")]
    public async Task ExclusionConstraint_UnnamedName_MatchesThePostgresConvention(
        string sql, string expected)
    {
        var model = await BuildModelAsync(sql);

        Assert.Equal(expected, ExclusionOf(model).Name);
    }

    // All three spellings of a built-in operator collapse to one token. Measured: PostgreSQL
    // reports an operator resolved in pg_catalog unqualified, so `OPERATOR(pg_catalog.=)` comes
    // back as a bare `=`. Keeping them apart would make two of the three re-diff forever.
    [Theory]
    [InlineData("EXCLUDE (a WITH =)")]
    [InlineData("EXCLUDE (a WITH OPERATOR(=))")]
    [InlineData("EXCLUDE (a WITH OPERATOR(pg_catalog.=))")]
    public async Task ExclusionConstraint_BuiltInOperatorSpellings_AllCanonicalizeToTheBareName(
        string clause)
    {
        var model = await BuildModelAsync($"CREATE TABLE t (a integer, {clause});");

        Assert.Equal("=", OperatorOf(Assert.Single(ElementsOf(ExclusionOf(model)))));
    }

    // An operator in any other schema keeps its qualifier, because the catalog keeps it too.
    [Fact]
    public async Task ExclusionConstraint_SchemaQualifiedOperator_KeepsItsSchema()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, EXCLUDE (a WITH OPERATOR(myops.===)));");

        Assert.Equal("myops.===", OperatorOf(Assert.Single(ElementsOf(ExclusionOf(model)))));
    }

    [Fact]
    public async Task ExclusionConstraint_WhereClause_ReachesTheModel()
    {
        var model = await BuildModelAsync(
            """
            CREATE TABLE t (
                room integer,
                during tstzrange,
                active boolean,
                EXCLUDE USING gist (room WITH =, during WITH &&) WHERE (active)
            );
            """);

        Assert.NotNull(
            ExclusionOf(model).GetProperty<string>(PostgresPropertyNames.FilterPredicate));
    }

    // The predicate compares by its canonical form, not its raw text. Measured, PostgreSQL
    // rewrites what it is given -- a declared `WHERE (active)` comes back as `WHERE active` --
    // so comparing the raw spelling would report a change that is not one.
    [Fact]
    public async Task ExclusionConstraint_WhereClause_IsCanonicalizedForComparison()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, active boolean, EXCLUDE (a WITH =) WHERE (active));");

        var exclusion = ExclusionOf(model);

        Assert.NotNull(
            exclusion.GetProperty<string>(PostgresPropertyNames.NormalizedFilterPredicate));

        // The raw spelling is kept for scripting but excluded from identity.
        var raw = Assert.Single(exclusion.Properties,
            p => p.Name == PostgresPropertyNames.FilterPredicate);

        Assert.False(raw.ParticipatesInIdentity);
    }

    [Fact]
    public async Task ExclusionConstraint_WithoutWhereClause_StoresNoPredicate()
    {
        var model = await BuildModelAsync("CREATE TABLE t (a integer, EXCLUDE (a WITH =));");

        Assert.Null(
            ExclusionOf(model).GetProperty<string>(PostgresPropertyNames.FilterPredicate));
    }

    // NOT DEFERRABLE INITIALLY IMMEDIATE is the Postgres default and is what pg_constraint
    // reports for a plain constraint, so neither flag is stored unless it is set.
    [Fact]
    public async Task ExclusionConstraint_NotDeferrable_StoresNeitherFlag()
    {
        var model = await BuildModelAsync("CREATE TABLE t (a integer, EXCLUDE (a WITH =));");

        var exclusion = ExclusionOf(model);

        Assert.Null(exclusion.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable));
        Assert.Null(exclusion.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred));
    }

    [Fact]
    public async Task ExclusionConstraint_Deferrable_ReachesTheModel()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, EXCLUDE (a WITH =) DEFERRABLE INITIALLY DEFERRED);");

        var exclusion = ExclusionOf(model);

        Assert.True(exclusion.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable));
        Assert.True(exclusion.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred));
    }

    [Fact]
    public async Task ExclusionConstraint_IncludeAndStorageParameters_ReachTheModel()
    {
        var model = await BuildModelAsync(
            """
            CREATE TABLE t (
                a integer,
                c integer,
                CONSTRAINT x EXCLUDE (a WITH =) INCLUDE (c) WITH (fillfactor = 70)
            );
            """);

        var exclusion = ExclusionOf(model);

        Assert.Equal(["c"],
            (exclusion.GetRelationship(PostgresRelationshipNames.IncludedColumns)?.Entries ?? [])
                .OfType<Reference>()
                .Select(r => r.Name.Split('.')[^1]));

        Assert.Equal("fillfactor=70",
            exclusion.GetProperty<string>(PostgresPropertyNames.StorageParameters));
    }

    // A btree exclusion constraint's backing index reports indoption exactly as an ordinary
    // index does (measured), so the implicit ASC / NULLS LAST defaults are filled in for btree
    // and left absent for any other access method -- matching what extraction will read back.
    [Fact]
    public async Task ExclusionConstraint_BtreeKeyOrdering_ReachesTheModel()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, b integer, EXCLUDE (a DESC WITH =, b WITH =));");

        var elements = ElementsOf(ExclusionOf(model));

        Assert.False(KeyOf(elements[0]).GetProperty<bool?>(PostgresPropertyNames.IsAscending));
        Assert.True(KeyOf(elements[1]).GetProperty<bool?>(PostgresPropertyNames.IsAscending));
    }

    [Fact]
    public async Task ExclusionConstraint_NonBtreeMethod_StoresNoOrdering()
    {
        var model = await BuildModelAsync(
            """
            CREATE TABLE t (
                room integer,
                during tstzrange,
                EXCLUDE USING gist (room WITH =, during WITH &&)
            );
            """);

        var elements = ElementsOf(ExclusionOf(model));

        Assert.Null(KeyOf(elements[0]).GetProperty<bool?>(PostgresPropertyNames.IsAscending));
    }

    [Fact]
    public async Task ExclusionConstraint_IsScriptedInline()
    {
        var script = await ScriptFromEmptyAsync(Booking);

        Assert.Contains(
            "CONSTRAINT \"no_overlap\" EXCLUDE USING gist (\"room\" WITH =, \"during\" WITH &&)",
            script);
    }

    // btree is the default access method, so USING btree is redundant in the emitted DDL --
    // the same suppression CREATE INDEX applies. The model still carries it, which is what
    // makes the two sides agree.
    [Fact]
    public async Task ExclusionConstraint_BtreeMethod_IsNotScripted()
    {
        var script = await ScriptFromEmptyAsync(
            "CREATE TABLE t (a integer, CONSTRAINT x EXCLUDE (a WITH =));");

        Assert.Contains("CONSTRAINT \"x\" EXCLUDE (\"a\" WITH =)", script);
        Assert.DoesNotContain("USING btree", script);
    }

    [Fact]
    public async Task ExclusionConstraint_WhereClause_IsScripted()
    {
        var script = await ScriptFromEmptyAsync(
            """
            CREATE TABLE t (
                a integer,
                active boolean,
                CONSTRAINT x EXCLUDE (a WITH =) WHERE (active)
            );
            """);

        Assert.Contains("WHERE (\"active\")", script);
    }

    [Fact]
    public async Task ExclusionConstraint_Deferrable_IsScripted()
    {
        var script = await ScriptFromEmptyAsync(
            "CREATE TABLE t (a integer, CONSTRAINT x EXCLUDE (a WITH =) DEFERRABLE INITIALLY DEFERRED);");

        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", script);
    }

    // A qualified operator can only be written through OPERATOR(...); a bare one is emitted
    // as-is, since it is punctuation and quoting it would change its meaning.
    [Fact]
    public async Task ExclusionConstraint_SchemaQualifiedOperator_IsScriptedThroughOperatorSyntax()
    {
        var script = await ScriptFromEmptyAsync(
            "CREATE TABLE t (a integer, CONSTRAINT x EXCLUDE (a WITH OPERATOR(myops.===)));");

        Assert.Contains("WITH OPERATOR(myops.===)", script);
    }

    [Fact]
    public async Task ExclusionConstraint_ExpressionKey_IsScriptedParenthesized()
    {
        var script = await ScriptFromEmptyAsync(
            "CREATE TABLE t (a text, CONSTRAINT x EXCLUDE (lower(a) WITH =));");

        Assert.Contains("EXCLUDE ((lower(\"a\")) WITH =)", script);
    }

    [Fact]
    public async Task ExclusionConstraint_IncludeAndStorageParameters_AreScripted()
    {
        var script = await ScriptFromEmptyAsync(
            """
            CREATE TABLE t (
                a integer,
                c integer,
                CONSTRAINT x EXCLUDE (a WITH =) INCLUDE (c) WITH (fillfactor = 70)
            );
            """);

        Assert.Contains("INCLUDE (\"c\") WITH (fillfactor=70)", script);
    }

    [Fact]
    public async Task ExclusionConstraint_IsDroppedThroughItsTable()
    {
        var provider = new PostgresDatabaseProvider("Host=unused");

        // The table stays in both models so the only drop is the constraint's own; dropping
        // the table would be gated as data loss, which is a different rule.
        var source = await BuildModelAsync(
            "CREATE TABLE booking (room integer, during tstzrange);");

        var comparison = SchemaCompare.Compare(
            provider,
            source,
            await BuildModelAsync(Booking),
            new DeployOptions { DropObjectsNotInSource = true });

        var script = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP CONSTRAINT IF EXISTS \"no_overlap\"", script);
    }

    // The constraint is emitted after the CHECK constraints and before the foreign keys, on
    // both builders. The Merkle hash is order-sensitive, so a mismatch here would make a
    // parsed model differ from an extracted one for reasons that are not real changes.
    [Fact]
    public async Task ExclusionConstraint_IsOrderedBetweenChecksAndForeignKeys()
    {
        var model = await BuildModelAsync(
            """
            CREATE TABLE parent (id integer PRIMARY KEY);
            CREATE TABLE t (
                id integer PRIMARY KEY,
                parent_id integer REFERENCES parent (id),
                a integer,
                CONSTRAINT c CHECK (a > 0),
                CONSTRAINT x EXCLUDE (a WITH =)
            );
            """);

        var types = model.Elements
            .Where(e => e.Type is PostgresElementTypes.SqlCheckConstraint
                or PostgresElementTypes.SqlExclusionConstraint
                or PostgresElementTypes.SqlForeignKeyConstraint)
            .Select(e => e.Type)
            .ToList();

        Assert.Equal(
            [
                PostgresElementTypes.SqlCheckConstraint,
                PostgresElementTypes.SqlExclusionConstraint,
                PostgresElementTypes.SqlForeignKeyConstraint,
            ],
            types);
    }

    // An exclusion constraint's name lives in the schema's relation namespace, because it is
    // index-backed. Two constraints in one schema therefore cannot share a name, and a
    // collision is reported rather than deployed.
    [Fact]
    public async Task ExclusionConstraint_DuplicateNameInOneSchema_IsABuildError()
    {
        var sql =
            """
            CREATE TABLE t1 (a integer, CONSTRAINT x EXCLUDE (a WITH =));
            CREATE TABLE t2 (a integer, CONSTRAINT x EXCLUDE (a WITH =));
            """;

        var exception = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildModelAsync(sql));

        Assert.Contains("x", exception.Message);
    }

    [Fact]
    public async Task ExclusionConstraint_KeyNamingAnUnknownColumn_IsABuildError()
    {
        var exception = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildModelAsync("CREATE TABLE t (a integer, EXCLUDE (nope WITH =));"));

        Assert.Contains("nope", exception.Message);
    }

    // Only the default tablespace is modeled, matching the rule CREATE INDEX and the other
    // index-backed constraints already apply (issues #160 and #210).
    [Fact]
    public async Task ExclusionConstraint_NonDefaultTablespace_IsRejected()
    {
        var exception = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildModelAsync(
                "CREATE TABLE t (a integer, EXCLUDE (a WITH =) USING INDEX TABLESPACE fast_ssd);"));

        Assert.Contains("fast_ssd", exception.Message);
    }
}
