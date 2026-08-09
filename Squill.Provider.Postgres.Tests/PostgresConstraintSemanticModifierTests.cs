using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Modeling and scripting of the constraint modifiers that were parsed and then dropped
/// (issue #205): <c>MATCH FULL</c> on a foreign key and <c>NO INHERIT</c> on a CHECK. Both
/// change what the deployed constraint enforces, so discarding them deployed a constraint the
/// source did not ask for.
///
/// The two modifiers the issue also lists are handled differently, and both decisions were
/// measured against a live PostgreSQL 18 rather than read off the grammar:
/// <list type="bullet">
/// <item><c>MATCH PARTIAL</c> parses, but the server answers "MATCH PARTIAL not yet
/// implemented", so the build rejects it rather than modeling something no server accepts.</item>
/// <item><c>NOT VALID</c> is accepted and <em>ignored</em> inside CREATE TABLE (the constraint
/// comes back <c>convalidated = t</c>) and honoured only by ALTER TABLE ADD CONSTRAINT. Since
/// Squill uses both paths depending on dependency order, modeling it would round-trip on one
/// and re-diff forever on the other, so it warns SQ1002 instead.</item>
/// </list>
/// </summary>
public class PostgresConstraintSemanticModifierTests
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()));

    private static async Task<IReadOnlyList<SqlSourceDiagnostic>> WarningsAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var result = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        return result.Warnings;
    }

    private static async Task<string> ScriptFromEmptyAsync(string sql)
    {
        var provider = new PostgresDatabaseProvider("Host=unused");
        var comparison = SchemaCompare.Compare(provider, await BuildModelAsync(sql), new Model());

        return new PostgresScriptGenerator().GenerateScript(comparison);
    }

    private static Element ConstraintOf(Model model, string type)
        => Assert.Single(model.Elements, e => e.Type == type);

    private const string ParentTable =
        "CREATE TABLE p (x integer, y integer, PRIMARY KEY (x, y));\n";

    [Fact]
    public async Task ForeignKey_MatchFull_ReachesTheModel()
    {
        var model = await BuildModelAsync(ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH FULL);");

        var fk = ConstraintOf(model, PostgresElementTypes.SqlForeignKeyConstraint);

        Assert.Equal("Full", fk.GetProperty<string>(PostgresPropertyNames.MatchType));
    }

    [Fact]
    public async Task ForeignKey_MatchSimple_StoresNoProperty()
    {
        // MATCH SIMPLE is the default and reports confmatchtype = 's', the same as an omitted
        // clause. Storing it would make the two spellings of one constraint hash differently.
        var model = await BuildModelAsync(ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH SIMPLE);");

        var fk = ConstraintOf(model, PostgresElementTypes.SqlForeignKeyConstraint);

        Assert.Null(fk.GetProperty<string>(PostgresPropertyNames.MatchType));
    }

    [Fact]
    public async Task ForeignKey_NoMatchClause_HashesTheSameAsMatchSimple()
    {
        // The strongest statement of the rule above: the two declarations are the same
        // constraint, so their models must be indistinguishable.
        var omitted = await BuildModelAsync(ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y));");

        var explicitSimple = await BuildModelAsync(ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH SIMPLE);");

        Assert.True(HashUtility.HashesEqual(omitted.Hash, explicitSimple.Hash));
    }

    [Fact]
    public async Task ForeignKey_MatchFull_HashesDifferentlyFromMatchSimple()
    {
        // The counterpart: MATCH FULL really is a different constraint, so it must not collide
        // with the default -- otherwise a deploy that changes one into the other is invisible.
        var full = await BuildModelAsync(ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH FULL);");

        var simple = await BuildModelAsync(ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y));");

        Assert.False(HashUtility.HashesEqual(full.Hash, simple.Hash));
    }

    [Fact]
    public async Task ForeignKey_InlineMatchFull_ReachesTheModel()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE p (x integer PRIMARY KEY);\n"
            + "CREATE TABLE t (a integer CONSTRAINT fk REFERENCES p (x) MATCH FULL);");

        var fk = ConstraintOf(model, PostgresElementTypes.SqlForeignKeyConstraint);

        Assert.Equal("Full", fk.GetProperty<string>(PostgresPropertyNames.MatchType));
    }

    [Fact]
    public async Task ForeignKey_MatchFull_IsScriptedBeforeKeyActions()
    {
        // The grammar is `REFERENCES ... key_match? key_actions?`, so MATCH after ON DELETE
        // would not parse. Asserting the rendered order is what catches a regression here.
        var sql = await ScriptFromEmptyAsync(ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH FULL ON DELETE CASCADE);");

        Assert.Contains(
            "REFERENCES \"p\" (\"x\", \"y\") MATCH FULL ON DELETE CASCADE",
            sql);
    }

    [Fact]
    public async Task ForeignKey_MatchSimple_ScriptsNoMatchClause()
    {
        var sql = await ScriptFromEmptyAsync(ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y));");

        Assert.DoesNotContain("MATCH", sql);
    }

    [Fact]
    public async Task ForeignKey_MatchPartial_IsRejected()
    {
        // PostgreSQL does not implement MATCH PARTIAL, so the build fails on the declaration
        // rather than the deploy failing later against a server that would refuse it anyway.
        // The refusal surfaces as a source-anchored SqlSourceException, like any other build
        // error, so the host can point at the offending statement.
        var exception = await Assert.ThrowsAsync<SqlSourceException>(() =>
            BuildModelAsync(ParentTable
                + "CREATE TABLE t (a integer, b integer, "
                + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH PARTIAL);"));

        Assert.Contains("MATCH PARTIAL", exception.Message);
        Assert.Equal("Test.sql", exception.SourceFile);
        Assert.IsType<NotSupportedException>(exception.InnerException);
    }

    [Fact]
    public async Task ForeignKey_InlineMatchPartial_IsRejected()
    {
        var exception = await Assert.ThrowsAsync<SqlSourceException>(() =>
            BuildModelAsync(
                "CREATE TABLE p (x integer PRIMARY KEY);\n"
                + "CREATE TABLE t (a integer REFERENCES p (x) MATCH PARTIAL);"));

        Assert.Contains("MATCH PARTIAL", exception.Message);
    }

    [Fact]
    public async Task CheckConstraint_NoInherit_ReachesTheModel()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, CONSTRAINT ck CHECK (a > 0) NO INHERIT);");

        var check = ConstraintOf(model, PostgresElementTypes.SqlCheckConstraint);

        Assert.True(check.GetProperty<bool?>(PostgresPropertyNames.IsNoInherit));
    }

    [Fact]
    public async Task CheckConstraint_InlineNoInherit_ReachesTheModel()
    {
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer CONSTRAINT ck CHECK (a > 0) NO INHERIT);");

        var check = ConstraintOf(model, PostgresElementTypes.SqlCheckConstraint);

        Assert.True(check.GetProperty<bool?>(PostgresPropertyNames.IsNoInherit));
    }

    [Fact]
    public async Task CheckConstraint_WithoutNoInherit_StoresNoProperty()
    {
        // Inheritable is the default (connoinherit = false), so an ordinary CHECK must gain no
        // property -- otherwise every existing CHECK in every project starts re-diffing.
        var model = await BuildModelAsync(
            "CREATE TABLE t (a integer, CONSTRAINT ck CHECK (a > 0));");

        var check = ConstraintOf(model, PostgresElementTypes.SqlCheckConstraint);

        Assert.Null(check.GetProperty<bool?>(PostgresPropertyNames.IsNoInherit));
    }

    [Fact]
    public async Task CheckConstraint_NoInherit_IsScriptedAfterThePredicate()
    {
        var sql = await ScriptFromEmptyAsync(
            "CREATE TABLE t (a integer, CONSTRAINT ck CHECK (a > 0) NO INHERIT);");

        Assert.Contains("CONSTRAINT \"ck\" CHECK (\"a\" > 0) NO INHERIT", sql);
    }

    [Fact]
    public async Task CheckConstraint_NoInherit_HashesDifferentlyFromInherited()
    {
        var noInherit = await BuildModelAsync(
            "CREATE TABLE t (a integer, CONSTRAINT ck CHECK (a > 0) NO INHERIT);");

        var inherited = await BuildModelAsync(
            "CREATE TABLE t (a integer, CONSTRAINT ck CHECK (a > 0));");

        Assert.False(HashUtility.HashesEqual(noInherit.Hash, inherited.Hash));
    }

    [Fact]
    public async Task NotValid_WarnsAndIsNotModeled()
    {
        // Not modeled, because CREATE TABLE ignores the clause -- but the author still gets
        // told, since the deployed constraint differs from what they wrote.
        var warning = Assert.Single(await WarningsAsync(ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y) NOT VALID);"));

        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
        Assert.Contains("NOT VALID", warning.Message);
        Assert.Equal("Test.sql", warning.SourceFile);
    }

    [Fact]
    public async Task NotValidCheck_Warns()
    {
        var warning = Assert.Single(await WarningsAsync(
            "CREATE TABLE t (a integer, CONSTRAINT ck CHECK (a > 0) NOT VALID);"));

        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
        Assert.Contains("NOT VALID", warning.Message);
    }

    [Fact]
    public async Task NotValid_DoesNotAffectDeferrability()
    {
        // NOT VALID and NOT DEFERRABLE both start with NOT; reading one as the other would
        // silently flip deferrability on a constraint that asked for neither.
        var model = await BuildModelAsync(ParentTable
            + "CREATE TABLE t (a integer, b integer, CONSTRAINT fk FOREIGN KEY (a, b) "
            + "REFERENCES p (x, y) DEFERRABLE INITIALLY DEFERRED NOT VALID);");

        var fk = ConstraintOf(model, PostgresElementTypes.SqlForeignKeyConstraint);

        Assert.True(fk.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable));
        Assert.True(fk.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred));
    }

    [Fact]
    public async Task OrdinaryConstraints_ProduceNoWarningsAndNoNewProperties()
    {
        // The regression guard for everything above: a project using none of these modifiers
        // must model exactly as it did before, or every existing constraint re-diffs.
        const string sql = ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y) ON DELETE CASCADE, "
            + "CONSTRAINT ck CHECK (a > 0));";

        Assert.Empty(await WarningsAsync(sql));

        var model = await BuildModelAsync(sql);

        var fk = ConstraintOf(model, PostgresElementTypes.SqlForeignKeyConstraint);
        var check = ConstraintOf(model, PostgresElementTypes.SqlCheckConstraint);

        Assert.Null(fk.GetProperty<string>(PostgresPropertyNames.MatchType));
        Assert.Null(check.GetProperty<bool?>(PostgresPropertyNames.IsNoInherit));
    }

    [Fact]
    public async Task GeneratedScript_ReparsesToAnIdenticalModel()
    {
        // The round trip through our own SQL: what we script must parse back to the same model,
        // or a deploy would emit SQL that re-diffs the moment it is read again.
        const string sql = ParentTable
            + "CREATE TABLE t (a integer, b integer, "
            + "CONSTRAINT fk FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH FULL ON DELETE CASCADE, "
            + "CONSTRAINT ck CHECK (a > 0) NO INHERIT);";

        var model = await BuildModelAsync(sql);
        var reparsed = await BuildModelAsync(await ScriptFromEmptyAsync(sql));

        Assert.True(HashUtility.HashesEqual(model.Hash, reparsed.Hash));
    }
}
