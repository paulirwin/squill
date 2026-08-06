using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Modeling and scripting of the index-shaped clauses on a PRIMARY KEY or UNIQUE constraint
/// (issue #210): INCLUDE, WITH (...) storage parameters and USING INDEX TABLESPACE. Each
/// already worked on the CREATE INDEX spelling and was silently dropped on the constraint
/// spelling, so the same declaration behaved differently depending on how it was written.
/// </summary>
public class PostgresConstraintIndexOptionTests
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

    private static Element ConstraintOf(Model model, string type)
        => Assert.Single(model.Elements, e => e.Type == type);

    private static IReadOnlyList<string> IncludedColumnsOf(Element constraint)
        => (constraint.GetRelationship(PostgresRelationshipNames.IncludedColumns)?.Entries ?? [])
            .OfType<Reference>()
            .Select(r => r.Name.Split('.')[^1])
            .ToList();

    [Fact]
    public async Task UniqueConstraint_Include_ReachesTheModel()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, b integer, CONSTRAINT uq UNIQUE (a) INCLUDE (b));");

        var unique = ConstraintOf(model, PostgresElementTypes.SqlUniqueConstraint);

        Assert.Equal(["b"], IncludedColumnsOf(unique));
    }

    [Fact]
    public async Task PrimaryKeyConstraint_Include_ReachesTheModel()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, b integer, CONSTRAINT pk PRIMARY KEY (a) INCLUDE (b));");

        var pk = ConstraintOf(model, PostgresElementTypes.SqlPrimaryKeyConstraint);

        Assert.Equal(["b"], IncludedColumnsOf(pk));
    }

    [Fact]
    public async Task UniqueConstraint_StorageParameters_ReachTheModel()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, CONSTRAINT uq UNIQUE (a) WITH (fillfactor = 70));");

        var unique = ConstraintOf(model, PostgresElementTypes.SqlUniqueConstraint);

        Assert.Equal(
            "fillfactor=70",
            unique.GetProperty<string>(PostgresPropertyNames.StorageParameters));
    }

    /// <summary>
    /// TABLESPACE is rejected rather than modeled, which is what CREATE INDEX already does
    /// (issue #160, measured there): an index in pg_default stores reltablespace = 0 exactly as
    /// one with no clause does, so the default spelling is a genuine no-op, while any other
    /// tablespace is a real placement decision that would be silently lost. Converging the two
    /// spellings on the same answer is the point of issue #210, so the constraint form must not
    /// quietly model something the index form refuses.
    /// </summary>
    [Fact]
    public async Task UniqueConstraint_NonDefaultTablespace_IsABuildError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync(
            "CREATE TABLE t (a integer, CONSTRAINT uq UNIQUE (a) USING INDEX TABLESPACE fast_ssd);"));

        Assert.Contains("TABLESPACE", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fast_ssd", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The default spelling is a no-op and builds, matching the index path.</summary>
    [Fact]
    public async Task UniqueConstraint_DefaultTablespace_IsAccepted()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, CONSTRAINT uq UNIQUE (a) USING INDEX TABLESPACE pg_default);");

        Assert.Equal("uq", ConstraintOf(model, PostgresElementTypes.SqlUniqueConstraint).Name);
    }

    /// <summary>
    /// The INCLUDE columns take part in the name PostgreSQL derives for an unnamed constraint.
    /// Measured: <c>UNIQUE (a, b) INCLUDE (c)</c> is named <c>t_a_b_c_key</c>, not
    /// <c>t_a_b_key</c>. Predicting it wrongly would mean the parsed model never hash-matches
    /// the extracted one, so the constraint would re-diff on every deploy.
    /// </summary>
    [Fact]
    public async Task UnnamedUnique_WithInclude_DerivesNameFromKeyAndIncludeColumns()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, b integer, c integer, UNIQUE (a, b) INCLUDE (c));");

        var unique = ConstraintOf(model, PostgresElementTypes.SqlUniqueConstraint);

        Assert.Equal("t_a_b_c_key", unique.Name);
    }

    /// <summary>
    /// A primary key's derived name ignores columns entirely (<c>&lt;table&gt;_pkey</c>), so
    /// INCLUDE cannot affect it. Measured, and asserted so the unique-side fix is not
    /// mistakenly generalized to the PK.
    /// </summary>
    [Fact]
    public async Task UnnamedPrimaryKey_WithInclude_KeepsThePkeyName()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, b integer, PRIMARY KEY (a) INCLUDE (b));");

        var pk = ConstraintOf(model, PostgresElementTypes.SqlPrimaryKeyConstraint);

        Assert.Equal("t_pkey", pk.Name);
    }

    /// <summary>
    /// An ordinary constraint carries none of these properties, so existing models are
    /// unchanged and cannot start re-diffing because of this feature.
    /// </summary>
    [Fact]
    public async Task OrdinaryUnique_CarriesNoIndexOptionProperties()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, CONSTRAINT uq UNIQUE (a));");

        var unique = ConstraintOf(model, PostgresElementTypes.SqlUniqueConstraint);

        Assert.Empty(IncludedColumnsOf(unique));
        Assert.Null(unique.GetProperty<string>(PostgresPropertyNames.StorageParameters));
    }

    // ---- scripting ----

    [Fact]
    public async Task UniqueConstraint_Include_IsScripted()
    {
        var script = await ScriptFromEmptyAsync(
            "CREATE TABLE t (a integer, b integer, CONSTRAINT uq UNIQUE (a) INCLUDE (b));");

        Assert.Contains("UNIQUE (\"a\") INCLUDE (\"b\")", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryKeyConstraint_Include_IsScripted()
    {
        var script = await ScriptFromEmptyAsync(
            "CREATE TABLE t (a integer, b integer, CONSTRAINT pk PRIMARY KEY (a) INCLUDE (b));");

        Assert.Contains("PRIMARY KEY (\"a\") INCLUDE (\"b\")", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniqueConstraint_StorageParameters_AreScripted()
    {
        var script = await ScriptFromEmptyAsync("""
CREATE TABLE t
(
    a integer,
    CONSTRAINT uq UNIQUE (a) WITH (fillfactor = 70)
);
""");

        Assert.Contains("WITH (fillfactor=70)", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-parsing the generated script must reproduce the same model, or the constraint would
    /// re-diff forever. The Docker-free half of the round-trip check.
    /// </summary>
    [Fact]
    public async Task ScriptedConstraintOptions_ReparseToAnIdenticalModel()
    {
        const string sql = """
CREATE TABLE t
(
    a integer,
    b integer,
    CONSTRAINT uq UNIQUE (a) INCLUDE (b) WITH (fillfactor = 70)
);
""";

        var original = await BuildModelAsync(sql);
        var reparsed = await BuildModelAsync(await ScriptFromEmptyAsync(sql));

        Assert.True(
            HashUtility.HashesEqual(original.Hash, reparsed.Hash),
            "A model built from the generated script must hash-match the original.");
    }
}
