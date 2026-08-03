using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// <see cref="ExpressionWalker"/> is reflection-driven precisely so it cannot forget a node type,
/// but "reaches every node" is a claim that has to be tested rather than assumed. These pin the
/// shapes an operand actually hides behind in this tree: a wrapper object that is not itself an
/// expression (a function call's <c>FunctionArgument</c>), a collection, a nested call, and a
/// cast.
///
/// <para>
/// The <c>IN</c> case earns its place. Its operands are built with <c>ToArray()</c> while
/// <c>ARRAY[...]</c> builds a <c>List</c>, and an earlier version of the walk treated an
/// <c>Expression[]</c> as a node to visit rather than a container to iterate — so the identical
/// predicate was walked or silently skipped depending on which spelling produced it. Nothing about
/// the two SQL forms suggests they differ; only a test over both catches it.
/// </para>
/// </summary>
public class WalkerSanityTests
{
    private static Expression ParsePredicate(string predicate)
    {
        var root = new AntlrPostgresParser().Parse(
            $"CREATE INDEX i ON t (c) WHERE {predicate};");
        return ((CreateIndexStatement)root.Statements[0]).WhereClause!;
    }

    [Theory]
    [InlineData("a > 0x19")]
    [InlineData("a > 1 AND b < 0x19")]
    [InlineData("f(a, 0x19) > 1")]
    [InlineData("a BETWEEN 1 AND 0x19")]
    [InlineData("a IN (1, 2, 0x19)")]
    [InlineData("(a + 0x19) > 1")]
    [InlineData("a > (0x19)::integer")]
    [InlineData("a = ANY (ARRAY[0x19])")]
    // IN builds its operand list with ToArray() while ARRAY[...] uses a List. Both spellings are
    // covered because the walk treats an array as a container rather than as a node — getting
    // that wrong made one of the two silently skip its operands.
    [InlineData("a IN (0x19)")]
    [InlineData("f(g(0x19)) > 1")]
    [InlineData("a > 0x19::integer")]
    [InlineData("-0x19 < a")]
    public void FindsHexLiteralWhereverItHides(string predicate)
    {
        var found = ExpressionWalker.DescendantsAndSelf(ParsePredicate(predicate))
            .OfType<LiteralExpression>()
            .Count(l => l.Radix != IntegerLiteralRadix.Decimal);

        Assert.True(found >= 1, $"walker missed the literal in: {predicate}");
    }

    [Fact]
    public void DoesNotDoubleVisit()
    {
        var found = ExpressionWalker.DescendantsAndSelf(ParsePredicate("a > 0x19"))
            .OfType<LiteralExpression>()
            .Count(l => l.Radix != IntegerLiteralRadix.Decimal);

        Assert.Equal(1, found);
    }

    [Theory]
    [InlineData("a > '0x19'")]
    [InlineData("a > 25")]
    public void IgnoresLookalikes(string predicate)
    {
        Assert.DoesNotContain(
            ExpressionWalker.DescendantsAndSelf(ParsePredicate(predicate)).OfType<LiteralExpression>(),
            l => l.Radix != IntegerLiteralRadix.Decimal);
    }
}
