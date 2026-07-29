using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Issue #156: redefining a CHECK predicate or a generated column's expression under the same
/// name must be seen as a change. It previously was not — both expressions were excluded from the
/// Merkle hash, so two sources differing only in the expression hashed EQUAL, no delta was
/// produced, and the deploy reported success while the old predicate stayed enforced.
///
/// They now take part in identity through a canonical form (see
/// <see cref="ExpressionNormalizer"/>), which is what lets a declared predicate compare against
/// one read back from information_schema even though both engines rewrite the text they store.
/// </summary>
public class MariaDbExpressionIdentityTests
{
    private static async Task<Model> BuildModelAsync(string sql)
    {
        var parser = new AntlrMariaDbParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(
            workspace, parser, new MariaDb12DatabaseSchemaProvider()).ExtractModelAsync()).Model;
    }

    private static string CheckTable(string predicate) => $"""
CREATE TABLE People
(
    Id            int NOT NULL PRIMARY KEY,
    DriverLicense int NOT NULL,
    CONSTRAINT CK_People CHECK ({predicate})
);
""";

    private static string GeneratedTable(string expression) => $"""
CREATE TABLE Tally
(
    Id  int NOT NULL PRIMARY KEY,
    X   int NOT NULL,
    Y   int NOT NULL,
    Sum int GENERATED ALWAYS AS ({expression}) STORED
);
""";

    /// <summary>
    /// The defect itself, asserted without a database: two sources differing only in the CHECK
    /// predicate must not hash-equal each other.
    /// </summary>
    [Fact]
    public async Task ChangedCheckPredicate_ChangesTheModelHash()
    {
        var loose = await BuildModelAsync(CheckTable("DriverLicense > 0"));
        var tight = await BuildModelAsync(CheckTable("DriverLicense > 10"));

        Assert.False(HashUtility.HashesEqual(loose.Hash, tight.Hash));
    }

    /// <summary>
    /// The same for a generated column: changing the expression must change the hash, or rows
    /// keep being computed with the old one.
    /// </summary>
    [Fact]
    public async Task ChangedGeneratedExpression_ChangesTheModelHash()
    {
        var sum = await BuildModelAsync(GeneratedTable("X + Y"));
        var difference = await BuildModelAsync(GeneratedTable("X - Y"));

        Assert.False(HashUtility.HashesEqual(sum.Hash, difference.Hash));
    }

    /// <summary>
    /// The property this fix must not break: a predicate written in a different but EQUIVALENT
    /// spelling must keep the same hash, since that is how a declared predicate meets the one the
    /// engines report back. Each right-hand spelling is one an engine actually produces.
    /// </summary>
    [Theory]
    [InlineData("DriverLicense > 0", "DriverLicense > 0")]
    // MariaDB backtick-quotes identifiers and lower-cases keywords.
    [InlineData("DriverLicense > 0", "`DriverLicense` > 0")]
    // MySQL additionally wraps the whole predicate in parentheses.
    [InlineData("DriverLicense > 0", "(`DriverLicense` > 0)")]
    [InlineData("DriverLicense > 0 AND Id > 0", "(`DriverLicense` > 0) and (`Id` > 0)")]
    [InlineData("DriverLicense > 0", "DriverLicense>0")]
    public async Task EquivalentPredicates_KeepTheSameHash(string left, string right)
    {
        var first = await BuildModelAsync(CheckTable(left));
        var second = await BuildModelAsync(CheckTable(right));

        Assert.True(HashUtility.HashesEqual(first.Hash, second.Hash));
    }

    /// <summary>
    /// Identifier case is preserved, so two predicates over genuinely different columns do not
    /// collapse together.
    /// </summary>
    [Fact]
    public async Task DifferentColumns_ChangeTheModelHash()
    {
        var byLicense = await BuildModelAsync(CheckTable("DriverLicense > 0"));
        var byId = await BuildModelAsync(CheckTable("Id > 0"));

        Assert.False(HashUtility.HashesEqual(byLicense.Hash, byId.Hash));
    }

    /// <summary>
    /// The canonical form is carried as its own property so the raw expression stays available
    /// for scripting: the script must reproduce what the user declared, not the normalized form.
    /// </summary>
    [Fact]
    public async Task RawExpression_IsPreservedForScripting()
    {
        var model = await BuildModelAsync(CheckTable("DriverLicense > 0"));

        var check = Assert.Single(model.Elements,
            e => e.Type == MariaDbElementTypes.SqlCheckConstraint);

        Assert.Equal("DriverLicense > 0",
            check.GetProperty<string>(MariaDbPropertyNames.CheckExpression));
    }
}
