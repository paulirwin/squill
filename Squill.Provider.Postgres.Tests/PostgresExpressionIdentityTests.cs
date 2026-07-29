using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Issue #156: redefining a CHECK predicate or a generated column's expression under the same
/// name must be seen as a change. It previously was not — both expressions were excluded from the
/// Merkle hash, so changing only the expression changed no hash, produced no delta, and the
/// deploy reported success while the old predicate stayed enforced.
///
/// They now take part in identity through a canonical form (see
/// <see cref="ExpressionNormalizer"/>), which is what lets a declared predicate compare against
/// one read back from the catalog even though PostgreSQL rewrites the text it stores.
/// </summary>
public class PostgresExpressionIdentityTests
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()));

    private static string CheckTable(string predicate) => $"""
CREATE TABLE people
(
    id             integer PRIMARY KEY,
    driver_license integer NOT NULL,
    CONSTRAINT ck_people CHECK ({predicate})
);
""";

    private static string GeneratedTable(string expression) => $"""
CREATE TABLE tally
(
    id  integer PRIMARY KEY,
    x   integer NOT NULL,
    y   integer NOT NULL,
    sum integer GENERATED ALWAYS AS ({expression}) STORED
);
""";

    /// <summary>
    /// The defect itself, asserted without a database: two sources differing only in the CHECK
    /// predicate must not hash-equal each other.
    /// </summary>
    [Fact]
    public async Task ChangedCheckPredicate_ChangesTheModelHash()
    {
        var loose = await BuildModelAsync(CheckTable("driver_license > 0"));
        var tight = await BuildModelAsync(CheckTable("driver_license > 10"));

        Assert.False(HashUtility.HashesEqual(loose.Hash, tight.Hash));
    }

    /// <summary>
    /// The same for a generated column: changing the expression must change the hash, or rows
    /// keep being computed with the old one.
    /// </summary>
    [Fact]
    public async Task ChangedGeneratedExpression_ChangesTheModelHash()
    {
        var sum = await BuildModelAsync(GeneratedTable("x + y"));
        var difference = await BuildModelAsync(GeneratedTable("x - y"));

        Assert.False(HashUtility.HashesEqual(sum.Hash, difference.Hash));
    }

    /// <summary>
    /// The property this fix must not break. Re-parsing the same source must produce the same
    /// hash, and — more importantly — a predicate written in a different but EQUIVALENT spelling
    /// must too, since that is how a declared predicate meets the one the catalog reports.
    /// </summary>
    [Theory]
    [InlineData("driver_license > 0", "driver_license > 0")]
    // Redundant parentheses and spacing are how PostgreSQL reports a predicate back.
    [InlineData("driver_license > 0", "(driver_license > 0)")]
    [InlineData("driver_license > 0", "driver_license>0")]
    // A cast PostgreSQL injects onto a literal says nothing the source chose.
    [InlineData("driver_license > 0", "driver_license > 0::integer")]
    public async Task EquivalentPredicates_KeepTheSameHash(string left, string right)
    {
        var first = await BuildModelAsync(CheckTable(left));
        var second = await BuildModelAsync(CheckTable(right));

        Assert.True(HashUtility.HashesEqual(first.Hash, second.Hash));
    }

    /// <summary>
    /// The canonical form is carried as its own property so the raw expression stays available
    /// for scripting: the script must reproduce what the user declared, not the normalized form.
    /// </summary>
    [Fact]
    public async Task RawExpression_IsPreservedForScripting()
    {
        var model = await BuildModelAsync(CheckTable("driver_license > 0"));

        var check = Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlCheckConstraint);

        Assert.Equal("\"driver_license\" > 0",
            check.GetProperty<string>(PostgresPropertyNames.CheckExpression));
    }

    /// <summary>
    /// An expression the normalizer cannot reduce falls back to the old behaviour — carried for
    /// scripting but left out of the hash — rather than being given a guessed canonical form. A
    /// wrong one would make an unchanged predicate look changed and redeploy it forever, which is
    /// worse than the known gap it degrades to.
    /// </summary>
    [Fact]
    public async Task UnnormalizableExpression_IsExcludedFromIdentity()
    {
        // BETWEEN SYMMETRIC expands to a four-way disjunction whose canonical form is not
        // established by measurement, so the normalizer refuses it.
        var model = await BuildModelAsync(CheckTable("driver_license BETWEEN SYMMETRIC 5 AND 1"));

        var check = Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlCheckConstraint);

        Assert.Null(check.GetProperty<string>(PostgresPropertyNames.NormalizedCheckExpression));

        Assert.False(Assert.Single(check.Properties,
            p => p.Name == PostgresPropertyNames.CheckExpression).ParticipatesInIdentity);
    }
}
