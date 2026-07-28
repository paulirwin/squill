using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    /// <summary>
    /// Repairs the operand of a grammar rule that recurses to a full <c>a_expr</c> on its
    /// right and so captures more than it should (issue #141).
    ///
    /// <c>a_expr_at_time_zone</c> (<c>a_expr_collate (AT TIME ZONE a_expr)?</c>) is the last
    /// rule still written this way, so <c>c AT TIME ZONE 'UTC' &gt; d</c> parses as
    /// <c>c AT TIME ZONE ('UTC' &gt; d)</c> — the tail swallows the lower-precedence comparison
    /// that should have stayed above it. PostgreSQL binds <c>AT TIME ZONE</c> tighter than a
    /// comparison (<c>gram.y</c> declares <c>%left AT</c> below the comparison operators), so
    /// the parse tree is simply wrong; the over-captured form is one the engine rejects
    /// outright with <c>function pg_catalog.timezone(boolean, ...) does not exist</c>.
    ///
    /// Every other tier of the ladder now recurses to the tier below it, so this is the only
    /// remaining caller. See #153 — once the upstream fix lands, this whole file goes away.
    ///
    /// The shape is recoverable without touching the grammar: the operand that truly belongs
    /// to the tight operator is the leftmost leaf of the over-captured subtree, so this splits
    /// that leaf off, hands it to <paramref name="buildTight"/>, and grafts the result back
    /// into the leaf's place — rotating the tree into the association Postgres would have
    /// produced.
    /// </summary>
    private static Expression RebalanceRightOperand(
        Expression captured, Func<Expression, Expression> buildTight)
    {
        // Nothing was over-captured: the tail is a single operand already.
        if (captured is not BinaryExpression binary)
        {
            return buildTight(captured);
        }

        // Only a binary chain is descended into. A parenthesized operand arrives as a
        // ParenthesizedExpression and so returns above — explicit grouping the author wrote
        // binds as written and must not be taken apart.
        return new BinaryExpression(
            RebalanceRightOperand(binary.Left, buildTight),
            binary.Operator,
            binary.Right);
    }
}
