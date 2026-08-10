using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// The constraint modifiers that parse and were then discarded by the visitor (issue #205):
/// <c>MATCH FULL</c> / <c>MATCH SIMPLE</c> on a foreign key, and <c>NO INHERIT</c> on a CHECK.
/// Each changes what the deployed constraint enforces, so dropping one deployed a constraint
/// with different semantics than the declaration.
///
/// <c>NOT VALID</c> is parsed here too, but deliberately reaches no syntax facet of its own:
/// measured against a live server, PostgreSQL <em>accepts and ignores</em> it inside
/// CREATE TABLE (the constraint comes back <c>convalidated = t</c>), and honours it only on
/// ALTER TABLE ADD CONSTRAINT. Modeling it would make the round trip depend on which deploy
/// path a constraint happened to take, so the provider warns SQ1002 instead.
///
/// <c>ON DELETE SET NULL (cols)</c> is deliberately absent: unlike these, it does not parse at
/// all, because the vendored <c>key_action</c> rule is <c>SET (NULL_P | DEFAULT)</c> with no
/// column list. That is a grammar gap rather than a visitor gap, reported upstream via #223.
/// </summary>
public class ConstraintSemanticModifierTests
{
    private static T ParseConstraint<T>(string text) where T : TableConstraint
    {
        var root = new AntlrPostgresParser().Parse(text);
        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        return Assert.Single(createTable.Elements
            .Select(e => e is NamedTableConstraint named ? named.Constraint : e)
            .OfType<T>());
    }

    private static T ParseColumnConstraint<T>(string text) where T : ColumnConstraint
    {
        var root = new AntlrPostgresParser().Parse(text);
        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        return Assert.Single(createTable.Elements.OfType<ColumnDefinition>()
            .SelectMany(c => c.Constraints)
            .OfType<T>());
    }

    [Fact]
    public void TableForeignKey_MatchFull_IsParsed()
    {
        var fk = ParseConstraint<ForeignKeyTableConstraint>(
            "CREATE TABLE t (a integer, b integer, "
            + "FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH FULL);");

        Assert.Equal(ForeignKeyMatchType.Full, fk.MatchType);
    }

    [Fact]
    public void TableForeignKey_MatchSimple_IsParsed()
    {
        var fk = ParseConstraint<ForeignKeyTableConstraint>(
            "CREATE TABLE t (a integer, b integer, "
            + "FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH SIMPLE);");

        Assert.Equal(ForeignKeyMatchType.Simple, fk.MatchType);
    }

    [Fact]
    public void TableForeignKey_MatchPartial_IsParsed()
    {
        // Parses, but the server rejects it ("MATCH PARTIAL not yet implemented"), so the
        // provider is what refuses it. The parser's job is only to carry the spelling.
        var fk = ParseConstraint<ForeignKeyTableConstraint>(
            "CREATE TABLE t (a integer, b integer, "
            + "FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH PARTIAL);");

        Assert.Equal(ForeignKeyMatchType.Partial, fk.MatchType);
    }

    [Fact]
    public void TableForeignKey_NoMatchClause_DefaultsToSimple()
    {
        // MATCH SIMPLE is the PostgreSQL default, so the omitted clause and the explicit
        // spelling must land on the same value or the two would re-diff against each other.
        var fk = ParseConstraint<ForeignKeyTableConstraint>(
            "CREATE TABLE t (a integer, b integer, FOREIGN KEY (a, b) REFERENCES p (x, y));");

        Assert.Equal(ForeignKeyMatchType.Simple, fk.MatchType);
    }

    [Fact]
    public void TableForeignKey_MatchFullWithKeyActions_ParsesBoth()
    {
        // key_match precedes key_actions in the grammar; reading one must not consume the other.
        var fk = ParseConstraint<ForeignKeyTableConstraint>(
            "CREATE TABLE t (a integer, b integer, "
            + "FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH FULL ON DELETE CASCADE ON UPDATE RESTRICT);");

        Assert.Equal(ForeignKeyMatchType.Full, fk.MatchType);
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
        Assert.Equal(ReferentialAction.Restrict, fk.OnUpdate);
    }

    [Fact]
    public void TableForeignKey_MatchFullWithDeferrable_ParsesBoth()
    {
        var fk = ParseConstraint<ForeignKeyTableConstraint>(
            "CREATE TABLE t (a integer, b integer, "
            + "FOREIGN KEY (a, b) REFERENCES p (x, y) MATCH FULL DEFERRABLE INITIALLY DEFERRED);");

        Assert.Equal(ForeignKeyMatchType.Full, fk.MatchType);
        Assert.True(fk.IsDeferrable);
        Assert.True(fk.IsInitiallyDeferred);
    }

    [Fact]
    public void ColumnForeignKey_MatchFull_IsParsed()
    {
        // key_match is reachable from colconstraintelem too, so the inline spelling must not
        // keep dropping what the table-level spelling now reads.
        var fk = ParseColumnConstraint<ForeignKeyColumnConstraint>(
            "CREATE TABLE t (a integer REFERENCES p (x) MATCH FULL);");

        Assert.Equal(ForeignKeyMatchType.Full, fk.MatchType);
    }

    [Fact]
    public void ColumnForeignKey_NoMatchClause_DefaultsToSimple()
    {
        var fk = ParseColumnConstraint<ForeignKeyColumnConstraint>(
            "CREATE TABLE t (a integer REFERENCES p (x));");

        Assert.Equal(ForeignKeyMatchType.Simple, fk.MatchType);
    }

    [Fact]
    public void TableCheck_NoInherit_IsParsed()
    {
        // At table level NO INHERIT arrives as a constraintattributeElem, not as no_inherit_.
        var check = ParseConstraint<CheckTableConstraint>(
            "CREATE TABLE t (a integer, CHECK (a > 0) NO INHERIT);");

        Assert.True(check.IsNoInherit);
    }

    [Fact]
    public void TableCheck_WithoutNoInherit_IsInherited()
    {
        var check = ParseConstraint<CheckTableConstraint>(
            "CREATE TABLE t (a integer, CHECK (a > 0));");

        Assert.False(check.IsNoInherit);
    }

    [Fact]
    public void ColumnCheck_NoInherit_IsParsed()
    {
        // Inline, NO INHERIT is the no_inherit_ rule on the CHECK alternative itself.
        var check = ParseColumnConstraint<CheckColumnConstraint>(
            "CREATE TABLE t (a integer CHECK (a > 0) NO INHERIT);");

        Assert.True(check.IsNoInherit);
    }

    [Fact]
    public void ColumnCheck_WithoutNoInherit_IsInherited()
    {
        var check = ParseColumnConstraint<CheckColumnConstraint>(
            "CREATE TABLE t (a integer CHECK (a > 0));");

        Assert.False(check.IsNoInherit);
    }

    [Fact]
    public void TableConstraint_NotValid_IsParsedAsNotValid()
    {
        // NOT VALID must not be mistaken for NOT DEFERRABLE: both alternatives begin with NOT,
        // and gating on the wrong keyword would silently flip deferrability.
        var fk = ParseConstraint<ForeignKeyTableConstraint>(
            "CREATE TABLE t (a integer, FOREIGN KEY (a) REFERENCES p (x) NOT VALID);");

        Assert.True(fk.IsNotValid);
        Assert.False(fk.IsDeferrable);
    }

    [Fact]
    public void TableConstraint_NotValidWithDeferrable_KeepsBothFacets()
    {
        var fk = ParseConstraint<ForeignKeyTableConstraint>(
            "CREATE TABLE t (a integer, FOREIGN KEY (a) REFERENCES p (x) DEFERRABLE NOT VALID);");

        Assert.True(fk.IsNotValid);
        Assert.True(fk.IsDeferrable);
    }

    [Fact]
    public void TableCheck_NotValid_IsParsed()
    {
        var check = ParseConstraint<CheckTableConstraint>(
            "CREATE TABLE t (a integer, CHECK (a > 0) NOT VALID);");

        Assert.True(check.IsNotValid);
    }
}
